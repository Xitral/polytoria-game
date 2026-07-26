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

namespace Polytoria.Creator.LSP;

public class LuaCompletionService(CreatorSession session)
{
	private readonly CreatorSession _session = session;
	private readonly string _workspacePath = session.ProjectFolderPath;
	private Process _luaLSProcess = null!;
	private LspClient _client = null!;
	private readonly Dictionary<string, int> _versions = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, string> _lastSyncedContent = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, long> _lastDependencyRevision = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, ModuleFileState> _moduleFileStates = new(StringComparer.OrdinalIgnoreCase);
	private long _moduleRevision;

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
		LuauModuleMapService.Generate(_session);
		RefreshModuleFileStates();

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

		_luaLSProcess.ErrorDataReceived += (sender, e) =>
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
		_client?.Dispose();
		if (_luaLSProcess != null && !_luaLSProcess.HasExited)
		{
			_luaLSProcess.Kill();
			_luaLSProcess.Dispose();
		}
	}

	public async Task OpenScriptAsync(string scriptPath)
	{
		bool moduleMapChanged = LuauModuleMapService.Generate(_session);
		bool moduleFilesChanged = RefreshModuleFileStates();
		if (moduleMapChanged && !moduleFilesChanged)
		{
			_moduleRevision++;
		}

		string content = File.ReadAllText(scriptPath);
		_versions[scriptPath] = 1;
		_lastSyncedContent[scriptPath] = content;
		_lastDependencyRevision[scriptPath] = _moduleRevision;
		await _client.DidOpenAsync(scriptPath, "luau", content);
	}

	public async Task CloseScriptAsync(string scriptPath)
	{
		_versions.Remove(scriptPath);
		_lastSyncedContent.Remove(scriptPath);
		_lastDependencyRevision.Remove(scriptPath);
		await _client.DidCloseAsync(scriptPath);
	}

	public async Task UpdateScriptChangeAsync(string scriptPath, string scriptContent)
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

	private async Task ReopenScriptAsync(string scriptPath, string scriptContent)
	{
		await _client.DidCloseAsync(scriptPath);
		await _client.DidOpenAsync(scriptPath, "luau", scriptContent);
		_versions[scriptPath] = 1;
		_lastSyncedContent[scriptPath] = scriptContent;
	}

	private bool RefreshModuleFileStates()
	{
		Dictionary<string, ModuleFileState> currentStates = new(StringComparer.OrdinalIgnoreCase);

		foreach (World world in _session.OpenedWorlds)
		{
			foreach (Instance instance in world.GetDescendants())
			{
				if (instance is not ModuleScript module)
				{
					continue;
				}

				string? linkedPath = module.LinkedScript?.LinkedPath;
				if (string.IsNullOrWhiteSpace(linkedPath))
				{
					continue;
				}

				string absolutePath = Path.IsPathRooted(linkedPath)
					? Path.GetFullPath(linkedPath)
					: Path.GetFullPath(Path.Join(_session.ProjectFolderPath, linkedPath));

				if (!File.Exists(absolutePath))
				{
					continue;
				}

				FileInfo file = new(absolutePath);
				currentStates[absolutePath] = new(file.LastWriteTimeUtc.Ticks, file.Length);
			}
		}

		bool changed = currentStates.Count != _moduleFileStates.Count;
		if (!changed)
		{
			foreach ((string path, ModuleFileState state) in currentStates)
			{
				if (!_moduleFileStates.TryGetValue(path, out ModuleFileState previousState) ||
					previousState != state)
				{
					changed = true;
					break;
				}
			}
		}

		_moduleFileStates.Clear();
		foreach ((string path, ModuleFileState state) in currentStates)
		{
			_moduleFileStates[path] = state;
		}

		if (changed)
		{
			_moduleRevision++;
		}

		return changed;
	}

	public async Task<List<CodeEditCompletionItem>> GetCompletionsAsync(CodeEditCompletionContext context, CancellationToken? cancelToken = null)
	{
		CancellationToken cancellationToken = cancelToken ?? CancellationToken.None;

		// Rebuild from the live DataModel so renames, reparenting, relinking, and
		// scripts located outside ScriptService are reflected immediately.
		bool moduleMapChanged = LuauModuleMapService.Generate(_session);
		bool moduleFilesChanged = RefreshModuleFileStates();
		if (moduleMapChanged && !moduleFilesChanged)
		{
			_moduleRevision++;
		}

		await UpdateScriptChangeAsync(context.ScriptPath, context.Content);

		long lastRevision = _lastDependencyRevision.TryGetValue(context.ScriptPath, out long revision)
			? revision
			: -1;
		bool moduleDependencyChanged = lastRevision != _moduleRevision;

		// Reopening is more reliable than sending an identical didChange notification.
		// It guarantees that Luau LSP reruns both source transforms and module inference.
		if (moduleDependencyChanged)
		{
			await ReopenScriptAsync(context.ScriptPath, context.Content);
			_lastDependencyRevision[context.ScriptPath] = _moduleRevision;
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

	private readonly record struct ModuleFileState(long LastWriteTicks, long Length);
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
