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

public class LuauToolboxModuleIntellisenseTest
{
	[Fact]
	public async Task ToolboxModuleExportsAreInferredBeforeModuleMapExists()
	{
		string repositoryRoot = FindRepositoryRoot();
		string workspacePath = Path.Join(Path.GetTempPath(), "polytoria-toolbox-lsp-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(workspacePath);

		Process? process = null;
		LspClient? client = null;

		try
		{
			string pluginDirectory = Path.Join(workspacePath, ".poly", "luau");
			string toolboxDirectory = Path.Join(workspacePath, "toolbox", "CFrameModule");
			string serverDirectory = Path.Join(workspacePath, "scripts", "server");
			Directory.CreateDirectory(pluginDirectory);
			Directory.CreateDirectory(toolboxDirectory);
			Directory.CreateDirectory(serverDirectory);

			File.WriteAllText(Path.Join(workspacePath, ".luaurc"), "{\n\t\"languageMode\": \"nocheck\"\n}\n");

			CopyPlugin(repositoryRoot, pluginDirectory, "polytoria-require.luau");
			CopyPlugin(repositoryRoot, pluginDirectory, "polytoria-module-types.luau");

			// The map intentionally does not exist yet. Toolbox files must still be
			// analyzed as nonstrict during their first workspace index.
			string modulePath = Path.Join(toolboxDirectory, "CFrameModule.luau");
			string moduleSource = """
				local CFrame = {}
				CFrame.Version = "V1.22"

				function CFrame.New(value)
					return value
				end

				function CFrame.lookAt(at, target)
					return at, target
				end

				return CFrame
				""";
			File.WriteAllText(modulePath, moduleSource);

			string serverPath = Path.Join(serverDirectory, "Test.server.luau");
			string serverSource = """
				local CFrameUtil = require("../../toolbox/CFrameModule/CFrameModule")
				CFrameUtil.
				""";
			File.WriteAllText(serverPath, serverSource);

			process = StartLanguageServer(repositoryRoot, workspacePath);
			_ = process.StandardError.ReadToEndAsync();

			client = new LspClient(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
			await client.InitializeAsync(workspacePath);
			await client.DidOpenAsync(modulePath, "luau", moduleSource);
			await client.DidOpenAsync(serverPath, "luau", serverSource);

			HashSet<string> labels = [];
			using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));

			while (!timeout.IsCancellationRequested)
			{
				try
				{
					LspCompletionItem[]? items = await client.RequestCompletionAsync(
						serverPath,
						1,
						"CFrameUtil.".Length,
						timeout.Token);

					labels = items?
						.Select(item => item.Label ?? "")
						.Where(label => label.Length > 0)
						.ToHashSet(StringComparer.Ordinal) ?? [];

					if (labels.Contains("New") && labels.Contains("Version") && labels.Contains("lookAt"))
					{
						break;
					}
				}
				catch (OperationCanceledException) when (timeout.IsCancellationRequested)
				{
					break;
				}

				await Task.Delay(200, timeout.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
			}

			Assert.Contains("New", labels);
			Assert.Contains("Version", labels);
			Assert.Contains("lookAt", labels);
		}
		finally
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

			if (Directory.Exists(workspacePath))
			{
				Directory.Delete(workspacePath, recursive: true);
			}
		}
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
