// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Polytoria.Creator.LSP;
using Polytoria.Creator.LSP.Schemas;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Polytoria.Tests;

public class LuauModuleMoveIntellisenseTest
{
	[Fact]
	public async Task MoveBeforeRequireEditAndExportChangesStayInSync()
	{
		CancellationToken testCancellation = TestContext.Current.CancellationToken;
		string repositoryRoot = FindRepositoryRoot();
		string workspacePath = Path.Join(Path.GetTempPath(), "polytoria-module-move-lsp-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(workspacePath);

		Process? process = null;
		LspClient? client = null;

		try
		{
			string pluginDirectory = Path.Join(workspacePath, ".poly", "luau");
			string moduleDirectory = Path.Join(workspacePath, "scripts", "modules");
			string serverDirectory = Path.Join(workspacePath, "scripts", "server");
			Directory.CreateDirectory(pluginDirectory);
			Directory.CreateDirectory(moduleDirectory);
			Directory.CreateDirectory(serverDirectory);

			File.WriteAllText(Path.Join(workspacePath, ".luaurc"), "{\n\t\"languageMode\": \"nocheck\"\n}\n");
			CopyPlugin(repositoryRoot, pluginDirectory, "polytoria-require.luau");
			CopyPlugin(repositoryRoot, pluginDirectory, "polytoria-module-types.luau");

			string mapPath = Path.Join(pluginDirectory, LuauModuleMapService.MapFileName);
			string modulePath = Path.Join(moduleDirectory, "MathUtil.luau");
			string serverPath = Path.Join(serverDirectory, "Test.server.luau");

			string initialModuleSource = """
				local MathUtil = {}
				MathUtil.Version = "1.0"

				function MathUtil.double(value)
					return value * 2
				end

				return MathUtil
				""";
			string initialServerSource = CreateServerSource("world.ScriptService.MathUtil");

			File.WriteAllText(modulePath, initialModuleSource);
			File.WriteAllText(serverPath, initialServerSource);
			WriteModuleMap(mapPath, "world.ScriptService.MathUtil");

			(process, client) = await StartClientAsync(repositoryRoot, workspacePath, modulePath, initialModuleSource, serverPath, initialServerSource, testCancellation);

			HashSet<string> labels = await WaitForCompletionsAsync(
				client,
				serverPath,
				labels => labels.Contains("Version") && labels.Contains("double"),
				testCancellation);
			Assert.Contains("Version", labels);
			Assert.Contains("double", labels);

			// Reproduce the order that previously broke completion: move the ModuleScript,
			// let Creator's module-map change restart the language server while the source
			// still contains the old require, and only then edit the require expression.
			WriteModuleMap(mapPath, "world.Environment.MathUtil");
			StopClient(process, client);
			process = null;
			client = null;

			(process, client) = await StartClientAsync(repositoryRoot, workspacePath, modulePath, initialModuleSource, serverPath, initialServerSource, testCancellation);

			string movedServerSource = CreateServerSource("world.Environment.MathUtil");
			File.WriteAllText(serverPath, movedServerSource);
			await client.DidChangeAsync(serverPath, movedServerSource, 2);

			labels = await WaitForCompletionsAsync(
				client,
				serverPath,
				labels => labels.Contains("Version") && labels.Contains("double"),
				testCancellation);
			Assert.Contains("Version", labels);
			Assert.Contains("double", labels);

			string addedExportSource = """
				local MathUtil = {}
				MathUtil.Version = "1.1"

				function MathUtil.double(value)
					return value * 2
				end

				function MathUtil.triple(value)
					return value * 3
				end

				return MathUtil
				""";
			File.WriteAllText(modulePath, addedExportSource);
			await client.DidChangeAsync(modulePath, addedExportSource, 2);

			labels = await WaitForCompletionsAsync(
				client,
				serverPath,
				labels => labels.Contains("double") && labels.Contains("triple"),
				testCancellation);
			Assert.Contains("double", labels);
			Assert.Contains("triple", labels);

			string removedExportSource = """
				local MathUtil = {}
				MathUtil.Version = "1.2"

				function MathUtil.triple(value)
					return value * 3
				end

				return MathUtil
				""";
			File.WriteAllText(modulePath, removedExportSource);
			await client.DidChangeAsync(modulePath, removedExportSource, 3);

			labels = await WaitForCompletionsAsync(
				client,
				serverPath,
				labels => labels.Contains("triple") && !labels.Contains("double"),
				testCancellation);
			Assert.Contains("triple", labels);
			Assert.DoesNotContain("double", labels);
		}
		finally
		{
			StopClient(process, client);

			if (Directory.Exists(workspacePath))
			{
				Directory.Delete(workspacePath, recursive: true);
			}
		}
	}

	private static string CreateServerSource(string moduleWorldPath)
	{
		return $"local MathUtil = require({moduleWorldPath})\nMathUtil.\n";
	}

	private static void WriteModuleMap(string mapPath, string moduleWorldPath)
	{
		string content = $"""
			# Polytoria Luau module map v2
			# S	world-id	source-file	source-world-path
			# M	world-id	module-world-path	module-file
			S	0	scripts/server/Test.server.luau	world.ScriptService.Test
			M	0	{moduleWorldPath}	scripts/modules/MathUtil.luau
			""";
		File.WriteAllText(mapPath, content.Replace("\r\n", "\n", StringComparison.Ordinal));
	}

	private static async Task<(Process Process, LspClient Client)> StartClientAsync(
		string repositoryRoot,
		string workspacePath,
		string modulePath,
		string moduleSource,
		string serverPath,
		string serverSource,
		CancellationToken cancellationToken)
	{
		Process process = StartLanguageServer(repositoryRoot, workspacePath);
		_ = process.StandardError.ReadToEndAsync(cancellationToken);

		LspClient client = new(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
		await client.InitializeAsync(workspacePath);
		await client.DidOpenAsync(modulePath, "luau", moduleSource);
		await client.DidOpenAsync(serverPath, "luau", serverSource);
		return (process, client);
	}

	private static async Task<HashSet<string>> WaitForCompletionsAsync(
		LspClient client,
		string serverPath,
		Func<HashSet<string>, bool> condition,
		CancellationToken testCancellation)
	{
		HashSet<string> labels = [];
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(testCancellation);
		timeout.CancelAfter(TimeSpan.FromSeconds(15));

		while (!timeout.IsCancellationRequested)
		{
			try
			{
				LspCompletionItem[]? items = await client.RequestCompletionAsync(
					serverPath,
					1,
					"MathUtil.".Length,
					timeout.Token);

				labels = items?
					.Select(item => item.Label ?? "")
					.Where(label => label.Length > 0)
					.ToHashSet(StringComparer.Ordinal) ?? [];

				if (condition(labels))
				{
					break;
				}

				await Task.Delay(200, timeout.Token);
			}
			catch (OperationCanceledException) when (timeout.IsCancellationRequested)
			{
				break;
			}
		}

		return labels;
	}

	private static Process StartLanguageServer(string repositoryRoot, string workspacePath)
	{
		string platformDirectory;
		string executableName;

		if (OperatingSystem.IsWindows())
		{
			platformDirectory = "windows";
			executableName = "luau-lsp.exe";
		}
		else if (OperatingSystem.IsLinux())
		{
			platformDirectory = "linux";
			executableName = "luau-lsp";
		}
		else if (OperatingSystem.IsMacOS())
		{
			platformDirectory = "macos";
			executableName = "luau-lsp";
		}
		else
		{
			throw new PlatformNotSupportedException("Luau LSP regression tests require Windows, Linux, or macOS.");
		}

		string executablePath = Path.Join(
			repositoryRoot,
			"Polytoria",
			"native",
			"luau-lsp",
			platformDirectory,
			executableName);

		Assert.True(File.Exists(executablePath), $"Bundled Luau LSP was not found at {executablePath}");

		if (!OperatingSystem.IsWindows())
		{
			File.SetUnixFileMode(
				executablePath,
				UnixFileMode.UserRead |
				UnixFileMode.UserWrite |
				UnixFileMode.UserExecute |
				UnixFileMode.GroupRead |
				UnixFileMode.GroupExecute |
				UnixFileMode.OtherRead |
				UnixFileMode.OtherExecute);
		}

		ProcessStartInfo startInfo = new()
		{
			FileName = executablePath,
			Arguments = "lsp --stdio",
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			WorkingDirectory = workspacePath
		};

		return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start the bundled Luau LSP.");
	}

	private static void StopClient(Process? process, LspClient? client)
	{
		if (process != null)
		{
			try
			{
				if (!process.HasExited)
				{
					process.Kill(entireProcessTree: true);
					process.WaitForExit(5000);
				}
			}
			catch (InvalidOperationException)
			{
				// The process exited while the test was cleaning up.
			}
		}

		client?.Dispose();
		process?.Dispose();
	}

	private static void CopyPlugin(string repositoryRoot, string destinationDirectory, string fileName)
	{
		string sourcePath = Path.Join(
			repositoryRoot,
			"Polytoria",
			"modules",
			"creator",
			"codehint",
			"luau",
			fileName);

		Assert.True(File.Exists(sourcePath), $"Luau plugin was not found at {sourcePath}");
		File.Copy(sourcePath, Path.Join(destinationDirectory, fileName));
	}

	private static string FindRepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory != null)
		{
			if (File.Exists(Path.Join(directory.FullName, "Polytoria.sln")))
			{
				return directory.FullName;
			}

			directory = directory.Parent;
		}

		throw new DirectoryNotFoundException("Could not locate the Polytoria repository root from the test output directory.");
	}
}
