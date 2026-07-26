// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Godot;
using Polytoria.Creator.LSP.Schemas;
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
	private readonly string _workspacePath = session.ProjectFolderPath;
	private Process _luaLSProcess = null!;
	private LspClient _client = null!;
	private readonly Dictionary<string, int> _versions = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, string> _lastSyncedContent = new(StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> _reportedInternalSources = new(StringComparer.OrdinalIgnoreCase);

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
			// Call publish in main thread
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
		string content = File.ReadAllText(scriptPath);
		_versions[scriptPath] = 1;
		_lastSyncedContent[scriptPath] = content;
		await _client.DidOpenAsync(scriptPath, "luau", content);
	}

	public async Task CloseScriptAsync(string scriptPath)
	{
		_versions.Remove(scriptPath);
		_lastSyncedContent.Remove(scriptPath);
		_reportedInternalSources.Remove(scriptPath);
		await _client.DidCloseAsync(scriptPath);
	}

	public async Task UpdateScriptChangeAsync(string scriptPath, string scriptContent)
	{
		if (_lastSyncedContent.TryGetValue(scriptPath, out string? previousContent) && previousContent == scriptContent)
		{
			return;
		}

		int version = _versions.TryGetValue(scriptPath, out int currentVersion) ? currentVersion + 1 : 2;
		_versions[scriptPath] = version;
		_lastSyncedContent[scriptPath] = scriptContent;
		await _client.DidChangeAsync(scriptPath, scriptContent, version);
	}

	public async Task<List<CodeEditCompletionItem>> GetCompletionsAsync(CodeEditCompletionContext context, CancellationToken? cancelToken = null)
	{
		CancellationToken cancellationToken = cancelToken ?? CancellationToken.None;

		// CodeEdit can request completion before its TextChanged callback finishes.
		// Synchronize the exact in-memory buffer before asking the language server.
		await UpdateScriptChangeAsync(context.ScriptPath, context.Content);

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
					9 => CodeEdit.CodeCompletionKind.Function, // Method
					3 => CodeEdit.CodeCompletionKind.Function, // Function
					21 => CodeEdit.CodeCompletionKind.Constant, // Constant
					7 => CodeEdit.CodeCompletionKind.Class, // Class
					13 => CodeEdit.CodeCompletionKind.Enum, // Enum
					6 => CodeEdit.CodeCompletionKind.Variable, // Variable
					20 => CodeEdit.CodeCompletionKind.Member, // EnumMember
					10 => CodeEdit.CodeCompletionKind.Member, // Property
					5 => CodeEdit.CodeCompletionKind.Member, // Field
					14 => CodeEdit.CodeCompletionKind.PlainText, // Keyword
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

		// Temporary proof-of-concept diagnostic. This confirms whether the plugin
		// transformed the source that Creator actually sent to Luau LSP.
		if (items.Count == 0 &&
			context.Content.Contains("world.ScriptService.MathUtil", StringComparison.Ordinal) &&
			_reportedInternalSources.Add(context.ScriptPath))
		{
			try
			{
				string? internalSource = await _client.RequestInternalSourceAsync(context.ScriptPath, cancellationToken);
				PT.Print("Luau LSP internal source for ", context.ScriptPath, ":\n", internalSource ?? "<null>");
			}
			catch (Exception ex)
			{
				PT.PrintErr("Failed to read Luau LSP internal source: ", ex.Message);
			}
		}

		return items;
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
