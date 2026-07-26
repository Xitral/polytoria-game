// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Polytoria.Tests;

public class LuauToolboxModuleIntellisenseTest
{
	[Fact]
	public async Task ToolboxModuleExportsAreInferredBeforeModuleMapExists()
	{
		CancellationToken testCancellation = TestContext.Current.CancellationToken;
		using LuauLspTestWorkspace workspace = new("polytoria-toolbox-lsp");

		// The module map intentionally does not exist yet. Toolbox files must still
		// be analyzed as nonstrict during their first workspace index.
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
		string modulePath = workspace.WriteFile("toolbox/CFrameModule/CFrameModule.luau", moduleSource);

		string serverSource = """
			local CFrameUtil = require("../../toolbox/CFrameModule/CFrameModule")
			CFrameUtil.
			""";
		string serverPath = workspace.WriteFile("scripts/server/Test.server.luau", serverSource);

		await workspace.RestartAsync(
			[
				(modulePath, moduleSource),
				(serverPath, serverSource)
			],
			testCancellation);

		HashSet<string> labels = await workspace.WaitForCompletionsAsync(
			serverPath,
			1,
			"CFrameUtil.".Length,
			labels => labels.Contains("New") && labels.Contains("Version") && labels.Contains("lookAt"),
			testCancellation);

		Assert.Contains("New", labels);
		Assert.Contains("Version", labels);
		Assert.Contains("lookAt", labels);
	}
}
