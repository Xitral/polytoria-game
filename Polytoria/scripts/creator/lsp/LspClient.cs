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

	private readonly TaskCompletionSource _pluginsReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly TaskCompletionSource _workspaceIndexed = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private int _pluginTimeoutReported;
	private int _indexTimeoutReported;

	public readonly Dictionary<string, string> LspPathToFull = new(StringComparer.OrdinalIgnoreCase);
	public readonly Dictionary<string, string> FullToLspPath = new(StringComparer.OrdinalIgnoreCase);
	public event Action<LspPublishDiagnosticsParams>? PublishDiagnostics;

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

	private async Task WaitForReadySignalAsync(
		TaskCompletionSource signal,
		string timeoutMessage,
		ref int timeoutReported,
		CancellationToken cancellationToken)
	{
		if (signal.Task.IsCompleted)
		{
			return;
		}

		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
		using CancellationTokenSource combined = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

		try
		{
			await signal.Task.WaitAsync(combined.Token);
		}
		catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
		{
			if (Interlocked.Exchange(ref timeoutReported, 1) == 0)
			{
				PT.PrintWarn(timeoutMessage);
			}

			// Do not impose the same timeout repeatedly if a future server version does
			// not emit the expected informational log message.
			signal.TrySetResult();
		}
	}

	private Task WaitUntilPluginsReadyAsync(CancellationToken cancellationToken = default)
	{
		return WaitForReadySignalAsync(
			_pluginsReady,
			"Timed out waiting for Luau LSP plugins; opening documents with the current language-server state",
			ref _pluginTimeoutReported,
			cancellationToken);
	}

	private Task WaitUntilWorkspaceIndexedAsync(CancellationToken cancellationToken)
	{
		return WaitForReadySignalAsync(
			_workspaceIndexed,
			"Timed out waiting for Luau LSP workspace indexing; continuing with the current language-server state",
			ref _indexTimeoutReported,
			cancellationToken);
	}

	public async Task DidOpenAsync(string path, string languageId, string text)
	{
		// Source transformations are selected when a managed document is opened.
		// Opening before the plugins load leaves ModuleScripts parsed in the project's
		// nocheck mode until a later save, which was the cause of moved modules only
		// acquiring autocomplete after Ctrl+S.
		await WaitUntilPluginsReadyAsync();

		string p = LspHelper.PathToUri(path);
		LspPathToFull[p] = path;
		FullToLspPath[path] = p;
		await SendNotificationAsync("textDocument/didOpen", new LspDidOpenParams
		{
			TextDocument = new LspTextDocumentItem
			{
				Uri = p,
				LanguageId = languageId,
				Version = 1,
				Text = text
			}
		});
	}

	public Task DidCloseAsync(string path)
	{
		if (FullToLspPath.Remove(path, out string? p)) LspPathToFull.Remove(p);
		return SendNotificationAsync("textDocument/didClose", new LspDidCloseParams
		{
			TextDocument = new() { Uri = LspHelper.PathToUri(path) }
		});
	}

	public Task DidChangeAsync(string path, string text, int version)
	{
		return SendNotificationAsync("textDocument/didChange", new LspDidChangeParams
		{
			TextDocument = new()
			{
				Uri = LspHelper.PathToUri(path),
				Version = version
			},
			ContentChanges = [new() { Text = text }]
		});
	}

	public async Task<LspCompletionItem[]?> RequestCompletionAsync(string path, int line, int character, CancellationToken cancellationToken)
	{
		await WaitUntilWorkspaceIndexedAsync(cancellationToken);

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
				_pluginsReady.TrySetResult();
			}

			if (messageText.StartsWith("Indexed ", StringComparison.Ordinal))
			{
				// Indexing necessarily happens after configuration/plugin setup. Mark both
				// ready as a resilient fallback if the plugin summary wording changes.
				_pluginsReady.TrySetResult();
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
}
