local MAP_FILE_NAME = "polytoria-module-map.tsv"
local TOOLBOX_FOLDER_NAME = "toolbox"

local function normalizePath(path: string): string
	local normalized = string.gsub(path, "\\", "/")
	normalized = string.gsub(normalized, "^/+", "")
	normalized = string.gsub(normalized, "/+$", "")
	return normalized
end

local function getProjectRelativePath(context: PluginContext): string?
	local rootPath = normalizePath(lsp.workspace.getRootUri().fsPath)
	local filePath = normalizePath(context.filePath)
	local prefix = rootPath .. "/"

	if string.lower(string.sub(filePath, 1, #prefix)) ~= string.lower(prefix) then
		return nil
	end

	return string.sub(filePath, #prefix + 1)
end

local function hasExplicitLanguageMode(source: string): boolean
	local firstLineEnd = string.find(source, "\n", 1, true)
	local firstLine = firstLineEnd == nil and source or string.sub(source, 1, firstLineEnd - 1)

	-- Strip an optional UTF-8 byte order mark before checking the hot comment.
	if string.sub(firstLine, 1, 3) == "\239\187\191" then
		firstLine = string.sub(firstLine, 4)
	end

	local trimmed = string.match(firstLine, "^%s*(.-)%s*$") or firstLine
	return trimmed == "--!strict"
		or trimmed == "--!nonstrict"
		or trimmed == "--!nocheck"
end

local function isToolboxSource(context: PluginContext): boolean
	local sourceFile = getProjectRelativePath(context)
	if sourceFile == nil then
		return false
	end

	local sourceKey = string.lower(normalizePath(sourceFile))
	local toolboxKey = string.lower(TOOLBOX_FOLDER_NAME)
	return sourceKey == toolboxKey or string.sub(sourceKey, 1, #toolboxKey + 1) == toolboxKey .. "/"
end

local function isMappedModule(context: PluginContext): boolean
	local sourceFile = getProjectRelativePath(context)
	if sourceFile == nil then
		return false
	end

	local rootUri = lsp.workspace.getRootUri()
	local mapUri = rootUri:joinPath(".poly", "luau", MAP_FILE_NAME)
	local ok, mapContent = pcall(function()
		return lsp.fs.readFile(mapUri)
	end)

	if not ok or type(mapContent) ~= "string" then
		return false
	end

	local sourceKey = string.lower(normalizePath(sourceFile))
	for line in string.gmatch(mapContent, "[^\r\n]+") do
		local moduleFile = string.match(line, "^M\t[^\t]*\t[^\t]*\t([^\t]*)$")
		if moduleFile ~= nil and string.lower(normalizePath(moduleFile)) == sourceKey then
			return true
		end
	end

	return false
end

return {
	transformSource = function(source: string, context: PluginContext): { TextEdit }?
		if hasExplicitLanguageMode(source) then
			return nil
		end

		-- Toolbox imports are written into the project's toolbox folder before the
		-- world-to-file module map is necessarily synchronized with every language
		-- server client. Treat those files as analyzable immediately so large imported
		-- ModuleScripts do not remain in the project's default nocheck mode.
		if not isToolboxSource(context) and not isMappedModule(context) then
			return nil
		end

		-- Creator projects currently default to nocheck, which disables Luau's type
		-- inference engine. Analyze ModuleScripts as nonstrict so their returned table
		-- shape is available to require() autocomplete without changing runtime code.
		return {
			{
				startLine = 1,
				startColumn = 1,
				endLine = 1,
				endColumn = 1,
				newText = "--!nonstrict\n",
			},
		}
	end,
} :: PluginApi
