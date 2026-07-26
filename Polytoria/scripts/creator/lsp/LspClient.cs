// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Polytoria.Creator.LSP.Schemas;
using Polytoria.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Polytoria.Creator.LSP;

public class LspClient(Stream input, Stream output) : LspClientBase(input, output)
{
	private static readonly string[] PluginPaths =
	[
		"./.poly/luau/polytoria-require.luau",
		"./.poly/luau/polytoria-module-types.luau"
	];

	private static readonly Regex DirectModuleRequireRegex = new(
		@"\brequire\s*\(\s*((?:world|script)(?:\s*\.\s*[A-Za-z_][A-Za-z0-9_]*)+)\s*\)",
		RegexOptions.CultureInvariant);

	private readonly TaskCompletionSource _workspaceIndexed = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly TaskCompletionSource _documentsReplayed = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly SemaphoreSlim _documentStateGate = new(1, 1);
	private readonly Dictionary<string, OpenDocument> _openDocuments = new(StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> _documentsNeedingTransformRefresh = new(StringComparer.OrdinalIgnoreCase);
	private int _indexTimeoutReported;
	private int _replayTimeoutReported;
	private int _pluginReplayStarted;

	public readonly Dictionary<string, string> LspPathToFull = new(StringComparer.OrdinalIgnoreCase);
	public readonly Dictionary<string, string> FullToLspPath = new(StringComparer.OrdinalIgnoreCase);
	public event Action<LspPublishDiagnosticsParams>? PublishDiagnostics;

	private readonly record struct OpenDocument(string LanguageId, string Text, int Version);

	public async Task InitializeAsync(string workspacePath)
	{
		LspInitializeParams initParams = new()
		{
			RootUri = LspHelper.PathToUri(workspacePath),
			Capabilities = new()
			{
				TextDocument = new()
				{
					Completion = new()
					{
						CompletionItem = new() { SnippetSupport = false }
					},
					Hover = new()
					{
						ContentFormat = ["plaintext"]
					},
					Synchronization = new()
					{
						DidSave = true,
						WillSave = true,
						WillSaveWaitUntil = true
					}
				},
				Workspace = new()
				{
					ApplyEdit = true,
					WorkspaceEdit = new() { DocumentChanges = true },
					Configuration = true,
					DidChangeWatchedFiles = new()
					{
						DynamicRegistration = true
					}
				},
				General = new()
				{
					PositionEncodings = ["utf-8"]
				}
			}
		};

		await SendRequestAsync<LspInitializeResult>("initialize", initParams);
		await SendNotificationAsync("initialized", new EmptyParams());
	}

	private static async Task<bool> WaitForReadySignalAsync(
		TaskCompletionSource signal,
		CancellationToken cancellationToken)
	{
		if (signal.Task.IsCompleted)
		{
			return true;
		}

		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
		using CancellationTokenSource combined = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

		try
		{
			await signal.Task.WaitAsync(combined.Token);
			return true;
		}
		catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
		{
			signal.TrySetResult();
			return false;
		}
	}

	private async Task WaitUntilWorkspaceIndexedAsync(CancellationToken cancellationToken)
	{
		if (!await WaitForReadySignalAsync(_workspaceIndexed, cancellationToken) &&
			Interlocked.Exchange(ref _indexTimeoutReported, 1) == 0)
		{
			PT.PrintWarn("Timed out waiting for Luau LSP workspace indexing; continuing with the current language-server state");
		}
	}

	private async Task WaitUntilDocumentsReplayedAsync(CancellationToken cancellationToken)
	{
		if (!await WaitForReadySignalAsync(_documentsReplayed, cancellationToken) &&
			Interlocked.Exchange(ref _replayTimeoutReported, 1) == 0)
		{
			PT.PrintWarn("Timed out waiting for Luau plugins to be reapplied to open scripts; continuing with the current language-server state");
		}
	}

	private static string GetDirectModuleRequireSignature(string source)
	{
		if (!source.Contains("require", StringComparison.Ordinal))
		{
			return "";
		}

		StringBuilder signature = new();
		foreach (Match match in DirectModuleRequireRegex.Matches(source))
		{
			foreach (char character in match.Groups[1].Value)
			{
				if (!char.IsWhiteSpace(character))
				{
					signature.Append(character);
				}
			}
			signature.Append('\n');
		}

		return signature.ToString();
	}

	public async Task DidOpenAsync(string path, string languageId, string text)
	{
		await _documentStateGate.WaitAsync();
		try
		{
			string p = LspHelper.PathToUri(path);
			LspPathToFull[p] = path;
			FullToLspPath[path] = p;
			_openDocuments[path] = new OpenDocument(languageId, text, 1);
			_documentsNeedingTransformRefresh.Remove(path);

			await SendDidOpenNotificationAsync(path, languageId, text, 1);
		}
		finally
		{
			_documentStateGate.Release();
		}
	}

	public async Task DidCloseAsync(string path)
	{
		await _documentStateGate.WaitAsync();
		try
		{
			_openDocuments.Remove(path);
			_documentsNeedingTransformRefresh.Remove(path);
			if (FullToLspPath.Remove(path, out string? p))
			{
				LspPathToFull.Remove(p);
			}

			await SendDidCloseNotificationAsync(path);
		}
		finally
		{
			_documentStateGate.Release();
		}
	}

	public async Task DidChangeAsync(string path, string text, int version)
	{
		await _documentStateGate.WaitAsync();
		try
		{
			bool directRequireChanged = false;
			string languageId = "luau";
			if (_openDocuments.TryGetValue(path, out OpenDocument existing))
			{
				languageId = existing.LanguageId;
				directRequireChanged = !string.Equals(
					GetDirectModuleRequireSignature(existing.Text),
					GetDirectModuleRequireSignature(text),
					StringComparison.Ordinal);
			}

			_openDocuments[path] = new OpenDocument(languageId, text, version);
			if (directRequireChanged)
			{
				// Luau LSP can retain the previous transformed require target after a
				// world-path edit. Reopen this one document immediately before its next
				// completion request so the plugin resolves against the current module map.
				_documentsNeedingTransformRefresh.Add(path);
			}

			await SendNotificationAsync("textDocument/didChange", new LspDidChangeParams
			{
				TextDocument = new()
				{
					Uri = LspHelper.PathToUri(path),
					Version = version
				},
				ContentChanges = [new() { Text = text }]
			});
		}
		finally
		{
			_documentStateGate.Release();
		}
	}

	private Task SendDidOpenNotificationAsync(string path, string languageId, string text, int version)
	{
		return SendNotificationAsync("textDocument/didOpen", new LspDidOpenParams
		{
			TextDocument = new LspTextDocumentItem
			{
				Uri = LspHelper.PathToUri(path),
				LanguageId = languageId,
				Version = version,
				Text = text
			}
		});
	}

	private Task SendDidCloseNotificationAsync(string path)
	{
		return SendNotificationAsync("textDocument/didClose", new LspDidCloseParams
		{
			TextDocument = new() { Uri = LspHelper.PathToUri(path) }
		});
	}

	private async Task ReplayOpenDocumentsAfterPluginLoadAsync()
	{
		await _documentStateGate.WaitAsync();
		try
		{
			KeyValuePair<string, OpenDocument>[] documents = [.. _openDocuments];
			foreach ((string path, OpenDocument document) in documents)
			{
				await SendDidCloseNotificationAsync(path);
				await SendDidOpenNotificationAsync(path, document.LanguageId, document.Text, document.Version);
			}

			_documentsNeedingTransformRefresh.Clear();
			if (documents.Length > 0)
			{
				PT.Print("Luau LSP reapplied plugins to ", documents.Length, " open script(s)");
			}
		}
		catch (Exception ex)
		{
			PT.PrintErr("Failed to reapply Luau plugins to open scripts: ", ex.Message);
		}
		finally
		{
			_documentsReplayed.TrySetResult();
			_documentStateGate.Release();
		}
	}

	private void StartDocumentReplayIfNeeded()
	{
		if (Interlocked.Exchange(ref _pluginReplayStarted, 1) == 0)
		{
			_ = ReplayOpenDocumentsAfterPluginLoadAsync();
		}
	}

	private async Task RefreshDocumentTransformIfNeededAsync(string path)
	{
		await _documentStateGate.WaitAsync();
		try
		{
			if (!_documentsNeedingTransformRefresh.Remove(path) ||
				!_openDocuments.TryGetValue(path, out OpenDocument document))
			{
				return;
			}

			await SendDidCloseNotificationAsync(path);
			await SendDidOpenNotificationAsync(path, document.LanguageId, document.Text, document.Version);
			PT.Print("Luau LSP refreshed require transforms for ", path);
		}
		finally
		{
			_documentStateGate.Release();
		}
	}

	public async Task<LspCompletionItem[]?> RequestCompletionAsync(string path, int line, int character, CancellationToken cancellationToken)
	{
		await WaitUntilWorkspaceIndexedAsync(cancellationToken);
		await WaitUntilDocumentsReplayedAsync(cancellationToken);
		await RefreshDocumentTransformIfNeededAsync(path);

		JsonElement rawResult = await SendRequestAsync<JsonElement>("textDocument/completion", new LspCompletionParams
		{
			TextDocument = new() { Uri = LspHelper.PathToUri(path) },
			Position = new() { Line = line, Character = character },
			Context = new() { TriggerKind = 1 }
		}, cancellationToken);

		if (rawResult.ValueKind == JsonValueKind.Array)
		{
			return rawResult.Deserialize(LspJsonContext.Default.LspCompletionItemArray);
		}

		if (rawResult.ValueKind == JsonValueKind.Object &&
			rawResult.TryGetProperty("items", out JsonElement items) &&
			items.ValueKind == JsonValueKind.Array)
		{
			return items.Deserialize(LspJsonContext.Default.LspCompletionItemArray);
		}

		return null;
	}

	public Task<string?> RequestInternalSourceAsync(string path, CancellationToken cancellationToken = default)
	{
		Dictionary<string, object> parameters = new()
		{
			["textDocument"] = new LspTextDocumentIdentifier
			{
				Uri = LspHelper.PathToUri(path)
			}
		};

		return SendRequestAsync<string>("luau-lsp/debug/viewInternalSource", parameters, cancellationToken);
	}

	protected override void HandleServerNotification(string method, JsonElement param)
	{
		if (method == "textDocument/publishDiagnostics")
		{
			LspPublishDiagnosticsParams? data = JsonSerializer.Deserialize(
				param.GetRawText(),
				LspJsonContext.Default.LspPublishDiagnosticsParams
			);

			if (data != null)
			{
				data.Uri = data.Uri.Replace("%3A", ":");
				PublishDiagnostics?.Invoke(data);
			}
		}
		else if ((method == "window/logMessage" || method == "window/showMessage") &&
			param.ValueKind == JsonValueKind.Object &&
			param.TryGetProperty("message", out JsonElement message))
		{
			string messageText = message.GetString() ?? "";
			PT.Print("Luau LSP: ", messageText);

			if (messageText.StartsWith($"Loaded {PluginPaths.Length} of {PluginPaths.Length} plugins", StringComparison.Ordinal))
			{
				StartDocumentReplayIfNeeded();
			}

			if (messageText.StartsWith("Indexed ", StringComparison.Ordinal))
			{
				StartDocumentReplayIfNeeded();
				_workspaceIndexed.TrySetResult();
			}
		}
	}

	private static Dictionary<string, object> CreateLuauConfiguration()
	{
		return new()
		{
			["platform"] = new Dictionary<string, object>
			{
				["type"] = "standard"
			},
			["plugins"] = new Dictionary<string, object>
			{
				["enabled"] = true,
				["paths"] = PluginPaths,
				["fileSystem"] = new Dictionary<string, object>
				{
					["enabled"] = true
				}
			}
		};
	}

	protected override async void HandleServerRequest(string method, JsonElement id, JsonElement? param)
	{
		try
		{
			if (method == "workspace/configuration")
			{
				int configurationCount = 1;
				if (param.HasValue &&
					param.Value.ValueKind == JsonValueKind.Object &&
					param.Value.TryGetProperty("items", out JsonElement items) &&
					items.ValueKind == JsonValueKind.Array)
				{
					configurationCount = items.GetArrayLength();
				}

				object[] configurations = new object[configurationCount];
				for (int i = 0; i < configurations.Length; i++)
				{
					configurations[i] = CreateLuauConfiguration();
				}

				PT.Print("Luau LSP requested ", configurationCount, " configuration entries; enabling ", PluginPaths.Length, " Polytoria plugins");

				LspResponse response = new()
				{
					Id = id.Clone(),
					Result = configurations
				};

				await WriteMessageAsync(response, CancellationToken.None);
			}
			else
			{
				LspResponse response = new()
				{
					Id = id.Clone(),
					Result = new EmptyParams()
				};

				await WriteMessageAsync(response, CancellationToken.None);
			}
		}
		catch (Exception ex)
		{
			PT.PrintErr($"Error handling server request '{method}': {ex.Message}");
		}
	}

	public override void Dispose()
	{
		_documentStateGate.Dispose();
		base.Dispose();
	}
}
