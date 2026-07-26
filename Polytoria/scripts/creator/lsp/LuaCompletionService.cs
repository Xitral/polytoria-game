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
	private readonly SemaphoreSlim _lspGate = new(1, 1);
	private Process _luaLSProcess = null!;
	private LspClient _client = null!;
	private readonly Dictionary<string, int> _versions = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, string> _lastSyncedContent = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, ModuleFileState> _moduleFileStates = new(StringComparer.OrdinalIgnoreCase);

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
			LuauModuleMapService.Generate(_session);
			RefreshModuleFileStates();
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
		StopLanguageServerCore();
	}

	public async Task OpenScriptAsync(string scriptPath)
	{
		await _lspGate.WaitAsync();
		try
		{
			bool workspaceChanged = LuauModuleMapService.Generate(_session);
			workspaceChanged |= RefreshModuleFileStates();

			if (workspaceChanged)
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

		return changed;
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

			bool workspaceChanged = LuauModuleMapService.Generate(_session);
			workspaceChanged |= RefreshModuleFileStates();

			// Luau LSP 1.68 can retain an old exported table shape after a transformed
			// module changes. A clean workspace restart reliably clears both the module
			// graph and plugin document caches while preserving every open editor buffer.
			if (workspaceChanged)
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
