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
	/// Rebuilds the map by scanning every open world. Prefer the tracked-script
	/// overload for editor hot paths.
	/// </summary>
	public static bool Generate(CreatorSession session)
	{
		List<Script> scripts = [];
		foreach (World world in session.OpenedWorlds)
		{
			foreach (Instance instance in world.GetDescendants())
			{
				if (instance is Script script)
				{
					scripts.Add(script);
				}
			}
		}

		return Generate(session, scripts);
	}

	/// <summary>
	/// Rebuilds the map from an already maintained script index and returns true
	/// when the generated contents changed.
	/// </summary>
	public static bool Generate(CreatorSession session, IEnumerable<Script> scripts)
	{
		// Creator and external VS Code sessions must use the same definitions,
		// plugins, filesystem permission, and full-typecheck completion mode.
		VSCodeConfigService.Ensure(session.ProjectFolderPath);

		string mapDirectory = Path.Join(session.PolyFolderPath, "luau");
		Directory.CreateDirectory(mapDirectory);

		Dictionary<World, string> worldIds = [];
		for (int worldIndex = 0; worldIndex < session.OpenedWorlds.Count; worldIndex++)
		{
			worldIds[session.OpenedWorlds[worldIndex]] = worldIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
		}

		SortedSet<string> rows = new(StringComparer.Ordinal);
		foreach (Script script in scripts)
		{
			if (!worldIds.TryGetValue(script.Root, out string? worldId))
			{
				continue;
			}

			ScriptEntry? entry = CreateEntry(session, script);
			if (!entry.HasValue)
			{
				continue;
			}

			ScriptEntry value = entry.Value;
			rows.Add(string.Join('\t', "S", worldId, value.ProjectPath, value.WorldPath));
			if (script is ModuleScript)
			{
				rows.Add(string.Join('\t', "M", worldId, value.WorldPath, value.ProjectPath));
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

	/// <summary>
	/// Returns whether an absolute editor file path is linked to a ModuleScript
	/// in any currently open world.
	/// </summary>
	public static bool IsLinkedModuleFile(CreatorSession session, string filePath)
	{
		string targetPath = Path.GetFullPath(filePath);

		foreach (World world in session.OpenedWorlds)
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
					: Path.GetFullPath(Path.Join(session.ProjectFolderPath, linkedPath));

				if (string.Equals(targetPath, absolutePath, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
		}

		return false;
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

		return new ScriptEntry(relativePath, worldPath);
	}

	private static bool ContainsUnsupportedMapCharacter(string value)
	{
		return value.Contains('\t') || value.Contains('\r') || value.Contains('\n');
	}

	private readonly record struct ScriptEntry(string ProjectPath, string WorldPath);
}
