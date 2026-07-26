// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Godot;
using Polytoria.Creator.LSP.Schemas;
using Polytoria.Datamodel;
using Polytoria.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DataModelScript = Polytoria.Datamodel.Script;

namespace Polytoria.Creator.LSP;

public class LuaCompletionService(CreatorSession session)
{
	private readonly CreatorSession _session = session;
	private readonly string _workspacePath = session.ProjectFolderPath;
	private readonly SemaphoreSlim _lspGate = new(1, 1);
	private Process _luaLSProcess = null!;
	private LspClient _client = null!;
	private readonly Dictionary<string, int> _versions = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, string> _lastSyncedContent = new(StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<World> _trackedWorlds = [];
	private readonly List<World> _worldOrder = [];
	private readonly HashSet<Instance> _trackedInstances = [];
	private readonly HashSet<DataModelScript> _trackedScripts = [];
	private readonly HashSet<string> _linkedModuleFiles = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, string> _lastSavedModuleContent = new(StringComparer.OrdinalIgnoreCase);
	private bool _moduleMapDirty = true;
	private bool _languageServerRefreshPending;

	public event Action<string, List<LspDiagnostic>>? PublishDiagnostics;

	public static readonly string[] LuaKeywords =
	[
		"and", "break", "do", "else", "elseif", "end",
		"false", "for", "function", "if",
		"in", "local", "nil", "not", "or", "repeat",
		"return", "then", "true", "until", "while",
		"continue", "const"
	];

	public async Task InitAsync()
	{
		await _lspGate.WaitAsync();
		try
		{
			SynchronizeTrackedWorlds();
			FlushModuleMapIfDirty();

			// The initial server starts after the current map is already on disk, so no
			// follow-up refresh is needed for that first generated snapshot.
			_languageServerRefreshPending = false;
			await StartLanguageServerCoreAsync();
		}
		finally
		{
			_lspGate.Release();
		}
	}

	private async Task StartLanguageServerCoreAsync()
	{
		ProcessStartInfo processStartInfo = new()
		{
			FileName = NativeBinHelper.ResolveLuauLspBinPath(),
			Arguments = "lsp --stdio --definitions=@poly=.poly/luau/def.d.luau",
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			WorkingDirectory = _workspacePath
		};

		_luaLSProcess = Process.Start(processStartInfo) ?? throw new Exception("Failed to start language server process");

		_luaLSProcess.ErrorDataReceived += (_, e) =>
		{
			if (!string.IsNullOrEmpty(e.Data))
			{
				PT.PrintErr($"Server Error: {e.Data}");
			}
		};

		_luaLSProcess.BeginErrorReadLine();

		PT.Print("LuaLS Started");

		_client = new LspClient(_luaLSProcess.StandardOutput.BaseStream, _luaLSProcess.StandardInput.BaseStream);
		await _client.InitializeAsync(_workspacePath);
		_client.PublishDiagnostics += OnPublishDiagnostics;

		PT.Print("Language server initialized at ", _workspacePath);
	}

	private void StopLanguageServerCore()
	{
		if (_client != null)
		{
			_client.PublishDiagnostics -= OnPublishDiagnostics;
		}

		// End the process first so the LSP reader's blocking stream read is released
		// immediately instead of waiting for its disposal timeout.
		if (_luaLSProcess != null)
		{
			try
			{
				if (!_luaLSProcess.HasExited)
				{
					_luaLSProcess.Kill();
				}
			}
			catch (InvalidOperationException)
			{
				// The process exited between HasExited and Kill.
			}
			finally
			{
				_luaLSProcess.Dispose();
				_luaLSProcess = null!;
			}
		}

		if (_client != null)
		{
			_client.Dispose();
			_client = null!;
		}
	}

	private async Task RestartLanguageServerCoreAsync()
	{
		Dictionary<string, string> openDocuments = new(_lastSyncedContent, StringComparer.OrdinalIgnoreCase);

		StopLanguageServerCore();
		await StartLanguageServerCoreAsync();

		_versions.Clear();
		foreach ((string scriptPath, string content) in openDocuments)
		{
			_versions[scriptPath] = 1;
			await _client.DidOpenAsync(scriptPath, "luau", content);
		}

		_languageServerRefreshPending = false;
		PT.Print("Luau LSP refreshed module types and reopened ", openDocuments.Count, " script(s)");
	}

	private void OnPublishDiagnostics(LspPublishDiagnosticsParams @params)
	{
		string normalizedUri = new Uri(@params.Uri).AbsoluteUri;
		if (_client.LspPathToFull.TryGetValue(normalizedUri, out string? fullPath))
		{
			Callable.From(() =>
			{
				PublishDiagnostics?.Invoke(fullPath, @params.Diagnostics);
			}).CallDeferred();
		}
	}

	public void Shutdown()
	{
		UntrackAllWorlds();
		StopLanguageServerCore();
	}

	public async Task OpenScriptAsync(string scriptPath)
	{
		await _lspGate.WaitAsync();
		try
		{
			SynchronizeTrackedWorlds();
			FlushModuleMapIfDirty();
			if (_languageServerRefreshPending)
			{
				await RestartLanguageServerCoreAsync();
			}

			string content = File.ReadAllText(scriptPath);
			_versions[scriptPath] = 1;
			_lastSyncedContent[scriptPath] = content;
			await _client.DidOpenAsync(scriptPath, "luau", content);
		}
		finally
		{
			_lspGate.Release();
		}
	}

	public async Task CloseScriptAsync(string scriptPath)
	{
		await _lspGate.WaitAsync();
		try
		{
			_versions.Remove(scriptPath);
			_lastSyncedContent.Remove(scriptPath);
			await _client.DidCloseAsync(scriptPath);
		}
		finally
		{
			_lspGate.Release();
		}
	}

	public async Task UpdateScriptChangeAsync(string scriptPath, string scriptContent)
	{
		await _lspGate.WaitAsync();
		try
		{
			await UpdateScriptChangeCoreAsync(scriptPath, scriptContent);
		}
		finally
		{
			_lspGate.Release();
		}
	}

	/// <summary>
	/// Records a completed editor save. Module export changes are coalesced into
	/// one clean language-server refresh on the next completion/open request.
	/// </summary>
	public async Task NotifyScriptSavedAsync(string scriptPath, string scriptContent)
	{
		await _lspGate.WaitAsync();
		try
		{
			await UpdateScriptChangeCoreAsync(scriptPath, scriptContent);
			SynchronizeTrackedWorlds();
			FlushModuleMapIfDirty();

			string absolutePath = Path.GetFullPath(scriptPath);
			if (_linkedModuleFiles.Contains(absolutePath))
			{
				bool contentChanged = !_lastSavedModuleContent.TryGetValue(absolutePath, out string? previousContent) ||
					previousContent != scriptContent;
				_lastSavedModuleContent[absolutePath] = scriptContent;

				if (contentChanged)
				{
					_languageServerRefreshPending = true;
				}
			}
		}
		finally
		{
			_lspGate.Release();
		}
	}

	private async Task UpdateScriptChangeCoreAsync(string scriptPath, string scriptContent)
	{
		if (_lastSyncedContent.TryGetValue(scriptPath, out string? previousContent) &&
			previousContent == scriptContent)
		{
			return;
		}

		int version = _versions.TryGetValue(scriptPath, out int currentVersion) ? currentVersion + 1 : 2;
		_versions[scriptPath] = version;
		_lastSyncedContent[scriptPath] = scriptContent;
		await _client.DidChangeAsync(scriptPath, scriptContent, version);
	}

	private void SynchronizeTrackedWorlds()
	{
		bool worldOrderChanged = _worldOrder.Count != _session.OpenedWorlds.Count;
		if (!worldOrderChanged)
		{
			for (int index = 0; index < _worldOrder.Count; index++)
			{
				if (!ReferenceEquals(_worldOrder[index], _session.OpenedWorlds[index]))
				{
					worldOrderChanged = true;
					break;
				}
			}
		}

		List<World> removedWorlds = [];
		foreach (World trackedWorld in _trackedWorlds)
		{
			if (!_session.OpenedWorlds.Contains(trackedWorld))
			{
				removedWorlds.Add(trackedWorld);
			}
		}

		foreach (World removedWorld in removedWorlds)
		{
			bool containedScripts = SubtreeContainsScript(removedWorld);
			UntrackSubtree(removedWorld);
			_trackedWorlds.Remove(removedWorld);
			if (containedScripts)
			{
				_moduleMapDirty = true;
			}
		}

		foreach (World world in _session.OpenedWorlds)
		{
			if (_trackedWorlds.Add(world))
			{
				if (TrackSubtree(world))
				{
					_moduleMapDirty = true;
				}
			}
		}

		if (worldOrderChanged)
		{
			_moduleMapDirty = true;
			_worldOrder.Clear();
			_worldOrder.AddRange(_session.OpenedWorlds);
		}
	}

	private bool TrackSubtree(Instance instance)
	{
		if (!_trackedInstances.Add(instance))
		{
			return SubtreeContainsScript(instance);
		}

		instance.ChildAdded.Connect(OnTrackedChildAdded);
		instance.ChildRemoved.Connect(OnTrackedChildRemoved);
		instance.Renamed.Connect(OnTrackedInstanceRenamed);

		bool containsScript = false;
		if (instance is DataModelScript script)
		{
			_trackedScripts.Add(script);
			script.PropertyChanged.Connect(OnTrackedScriptPropertyChanged);
			containsScript = true;
		}

		foreach (Instance child in instance.GetChildren())
		{
			containsScript |= TrackSubtree(child);
		}

		return containsScript;
	}

	private void UntrackSubtree(Instance instance)
	{
		if (!_trackedInstances.Remove(instance))
		{
			return;
		}

		foreach (Instance child in instance.GetChildren())
		{
			UntrackSubtree(child);
		}

		instance.ChildAdded.Disconnect(OnTrackedChildAdded);
		instance.ChildRemoved.Disconnect(OnTrackedChildRemoved);
		instance.Renamed.Disconnect(OnTrackedInstanceRenamed);

		if (instance is DataModelScript script)
		{
			script.PropertyChanged.Disconnect(OnTrackedScriptPropertyChanged);
			_trackedScripts.Remove(script);
		}
	}

	private void UntrackAllWorlds()
	{
		List<World> worlds = [.. _trackedWorlds];
		foreach (World world in worlds)
		{
			UntrackSubtree(world);
		}
		_trackedWorlds.Clear();
		_worldOrder.Clear();
	}

	private void OnTrackedChildAdded(Instance child)
	{
		if (TrackSubtree(child))
		{
			_moduleMapDirty = true;
		}
	}

	private void OnTrackedChildRemoved(Instance child)
	{
		bool containedScripts = SubtreeContainsScript(child);
		UntrackSubtree(child);
		if (containedScripts)
		{
			_moduleMapDirty = true;
		}
	}

	private void OnTrackedInstanceRenamed()
	{
		// Renaming any ancestor changes the LuaPath of scripts below it.
		_moduleMapDirty = true;
	}

	private void OnTrackedScriptPropertyChanged(string propertyName)
	{
		if (propertyName == nameof(DataModelScript.LinkedScript))
		{
			_moduleMapDirty = true;
		}
	}

	private static bool SubtreeContainsScript(Instance instance)
	{
		if (instance is DataModelScript)
		{
			return true;
		}

		foreach (Instance child in instance.GetChildren())
		{
			if (SubtreeContainsScript(child))
			{
				return true;
			}
		}

		return false;
	}

	private void FlushModuleMapIfDirty()
	{
		if (!_moduleMapDirty)
		{
			return;
		}

		_moduleMapDirty = false;
		bool mapChanged = LuauModuleMapService.Generate(_session, _trackedScripts);
		RebuildLinkedModuleFileIndex();

		if (mapChanged)
		{
			_languageServerRefreshPending = true;
		}
	}

	private void RebuildLinkedModuleFileIndex()
	{
		HashSet<string> currentFiles = new(StringComparer.OrdinalIgnoreCase);
		foreach (DataModelScript script in _trackedScripts)
		{
			if (script is not ModuleScript)
			{
				continue;
			}

			string? linkedPath = script.LinkedScript?.LinkedPath;
			if (string.IsNullOrWhiteSpace(linkedPath))
			{
				continue;
			}

			string absolutePath = Path.IsPathRooted(linkedPath)
				? Path.GetFullPath(linkedPath)
				: Path.GetFullPath(Path.Join(_session.ProjectFolderPath, linkedPath));
			currentFiles.Add(absolutePath);

			if (!_lastSavedModuleContent.ContainsKey(absolutePath) && File.Exists(absolutePath))
			{
				_lastSavedModuleContent[absolutePath] = File.ReadAllText(absolutePath);
			}
		}

		_linkedModuleFiles.Clear();
		foreach (string path in currentFiles)
		{
			_linkedModuleFiles.Add(path);
		}

		List<string> removedFiles = [];
		foreach (string path in _lastSavedModuleContent.Keys)
		{
			if (!currentFiles.Contains(path))
			{
				removedFiles.Add(path);
			}
		}
		foreach (string path in removedFiles)
		{
			_lastSavedModuleContent.Remove(path);
		}
	}

	public async Task<List<CodeEditCompletionItem>> GetCompletionsAsync(CodeEditCompletionContext context, CancellationToken? cancelToken = null)
	{
		CancellationToken cancellationToken = cancelToken ?? CancellationToken.None;
		await _lspGate.WaitAsync(cancellationToken);

		try
		{
			// Keep the exact in-memory source synchronized before taking a snapshot for
			// a possible language-server refresh.
			await UpdateScriptChangeCoreAsync(context.ScriptPath, context.Content);

			SynchronizeTrackedWorlds();
			FlushModuleMapIfDirty();

			// Module saves and hierarchy changes are coalesced. The ordinary completion
			// path performs no world traversal, file timestamp scan, or map rebuild.
			if (_languageServerRefreshPending)
			{
				await RestartLanguageServerCoreAsync();
			}

			LspCompletionItem[]? completionResult = await _client.RequestCompletionAsync(
				context.ScriptPath,
				context.CursorLine,
				context.CursorColumn,
				cancellationToken);

			List<CodeEditCompletionItem> items = [];

			if (completionResult != null)
			{
				foreach (LspCompletionItem item in completionResult)
				{
					CodeEdit.CodeCompletionKind kind = item.Kind switch
					{
						9 => CodeEdit.CodeCompletionKind.Function,
						3 => CodeEdit.CodeCompletionKind.Function,
						21 => CodeEdit.CodeCompletionKind.Constant,
						7 => CodeEdit.CodeCompletionKind.Class,
						13 => CodeEdit.CodeCompletionKind.Enum,
						6 => CodeEdit.CodeCompletionKind.Variable,
						20 => CodeEdit.CodeCompletionKind.Member,
						10 => CodeEdit.CodeCompletionKind.Member,
						5 => CodeEdit.CodeCompletionKind.Member,
						14 => CodeEdit.CodeCompletionKind.PlainText,
						_ => CodeEdit.CodeCompletionKind.PlainText,
					};

					items.Add(new()
					{
						DisplayText = item.Label ?? "",
						Kind = kind,
						Detail = item.Detail ?? "",
						InsertText = string.IsNullOrWhiteSpace(item.InsertText) ? item.Label ?? "" : item.InsertText
					});
				}
			}

			return items;
		}
		finally
		{
			_lspGate.Release();
		}
	}
}

public struct CodeEditCompletionItem
{
	public string DisplayText { get; set; }
	public CodeEdit.CodeCompletionKind Kind { get; set; }
	public string InsertText { get; set; }
	public string Detail { get; set; }
}

public struct CodeEditCompletionContext
{
	public string ScriptPath { get; set; }
	public string Content { get; set; }
	public int CursorLine { get; set; }
	public int CursorColumn { get; set; }
}
