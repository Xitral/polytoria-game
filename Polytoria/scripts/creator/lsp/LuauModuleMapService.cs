// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Polytoria.Datamodel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Script = Polytoria.Datamodel.Script;

namespace Polytoria.Creator.LSP;

/// <summary>
/// Generates the project-local mapping consumed by the Luau LSP require plugin.
/// Scripts and ModuleScripts may be linked anywhere in a world hierarchy and
/// anywhere inside the project folder.
/// </summary>
public static class LuauModuleMapService
{
	public const string MapFileName = "polytoria-module-map.tsv";

	private const string Header = "# Polytoria Luau module map v2\n" +
		"# S\tworld-id\tsource-file\tsource-world-path\n" +
		"# M\tworld-id\tmodule-world-path\tmodule-file\n";

	/// <summary>
	/// Rebuilds the map and returns true when its contents changed.
	/// </summary>
	public static bool Generate(CreatorSession session)
	{
		string mapDirectory = Path.Join(session.PolyFolderPath, "luau");
		Directory.CreateDirectory(mapDirectory);

		SortedSet<string> rows = new(StringComparer.Ordinal);

		for (int worldIndex = 0; worldIndex < session.OpenedWorlds.Count; worldIndex++)
		{
			World world = session.OpenedWorlds[worldIndex];
			string worldId = worldIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
			List<ScriptEntry> scripts = [];

			foreach (Instance instance in world.GetDescendants())
			{
				if (instance is not Script script)
				{
					continue;
				}

				ScriptEntry? entry = CreateEntry(session, script);
				if (entry.HasValue)
				{
					scripts.Add(entry.Value);
				}
			}

			foreach (ScriptEntry source in scripts)
			{
				rows.Add(string.Join('\t', "S", worldId, source.ProjectPath, source.WorldPath));

				if (source.Script is ModuleScript)
				{
					rows.Add(string.Join('\t', "M", worldId, source.WorldPath, source.ProjectPath));
				}
			}
		}

		StringBuilder output = new(Header);
		foreach (string row in rows)
		{
			output.AppendLine(row);
		}

		string mapPath = Path.Join(mapDirectory, MapFileName);
		string newContent = output.ToString();
		if (File.Exists(mapPath) && File.ReadAllText(mapPath) == newContent)
		{
			return false;
		}

		File.WriteAllText(mapPath, newContent);
		return true;
	}

	private static ScriptEntry? CreateEntry(CreatorSession session, Script script)
	{
		string? linkedPath = script.LinkedScript?.LinkedPath;
		if (string.IsNullOrWhiteSpace(linkedPath))
		{
			return null;
		}

		string absolutePath = Path.IsPathRooted(linkedPath)
			? Path.GetFullPath(linkedPath)
			: Path.GetFullPath(Path.Join(session.ProjectFolderPath, linkedPath));

		string relativePath = Path.GetRelativePath(session.ProjectFolderPath, absolutePath)
			.Replace('\\', '/');

		if (relativePath == ".." || relativePath.StartsWith("../", StringComparison.Ordinal))
		{
			return null;
		}

		string worldPath = script.LuaPath;
		if (ContainsUnsupportedMapCharacter(relativePath) || ContainsUnsupportedMapCharacter(worldPath))
		{
			return null;
		}

		return new ScriptEntry(script, relativePath, worldPath);
	}

	private static bool ContainsUnsupportedMapCharacter(string value)
	{
		return value.Contains('\t') || value.Contains('\r') || value.Contains('\n');
	}

	private readonly record struct ScriptEntry(Script Script, string ProjectPath, string WorldPath);
}
