local TARGET = "world.ScriptService.MathUtil"
local REPLACEMENT = '"../modules/MathUtil"'

local function getPosition(source: string, byteIndex: number): (number, number)
	local line = 1
	local lineStart = 1

	for i = 1, byteIndex - 1 do
		if string.byte(source, i) == 10 then
			line += 1
			lineStart = i + 1
		end
	end

	return line, byteIndex - lineStart + 1
end

return {
	transformSource = function(source: string, _context: PluginContext): { TextEdit }?
		local edits = {}
		local searchFrom = 1

		while true do
			local startIndex, endIndex = string.find(source, TARGET, searchFrom, true)

			if startIndex == nil or endIndex == nil then
				break
			end

			local startLine, startColumn = getPosition(source, startIndex)
			local endLine, endColumn = getPosition(source, endIndex + 1)

			table.insert(edits, {
				startLine = startLine,
				startColumn = startColumn,
				endLine = endLine,
				endColumn = endColumn,
				newText = REPLACEMENT,
			})

			searchFrom = endIndex + 1
		end

		if #edits == 0 then
			return nil
		end

		return edits
	end,
} :: PluginApi
