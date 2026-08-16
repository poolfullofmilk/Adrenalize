#Requires -Version 5.1
<#
    Checks .razor files against the razor conventions in CLAUDE.md.
    No off-the-shelf formatter honours these rules, so this reports violations
    and never rewrites a file. Exits 1 when anything is reported.

    Usage:
      pwsh RazorStyle.ps1
      pwsh RazorStyle.ps1 -Path Components/Pages
      pwsh RazorStyle.ps1 -Rule Alignment
#>
[CmdletBinding()]
param(
    [string]$Path = '.',
    [string[]]$Rule,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

$BigContainerTags = @(
    'MudTabs',
    'MudTable',
    'DataTable',
    'MudGrid',
    'MudDialog',
    'EditForm',
    'MudForm',
    'MudExpansionPanels',
    'MudDataGrid'
)

$violations = New-Object System.Collections.Generic.List[object]

function Add-Violation {
    param([string]$File, [int]$Line, [string]$RuleName, [string]$Message)
    if ($Rule -and $Rule -notcontains $RuleName) { return }
    $violations.Add(
        [pscustomobject]@{
            File    = $File
            Line    = $Line
            Rule    = $RuleName
            Message = $Message
        }
    )
}

# Walks A Razor File And Returns Its Open Tags With Attribute Positions
function Get-RazorTags {
    param([string]$Text)

    $tags = New-Object System.Collections.Generic.List[object]
    $length = $Text.Length
    $index = 0
    $line = 1
    $column = 1

    # Advances One Character Keeping Line And Column In Step
    function Step {
        param([int]$Count = 1)
        for ($step = 0; $step -lt $Count; $step++) {
            if ($script:index -ge $script:length) { return }
            if ($script:Text[$script:index] -eq "`n") {
                $script:line++
                $script:column = 1
            }
            else {
                $script:column++
            }
            $script:index++
        }
    }

    # Script Scope So The Nested Helper Shares Them
    $script:Text = $Text
    $script:index = $index
    $script:line = $line
    $script:column = $column
    $script:length = $length

    while ($script:index -lt $script:length) {
        $character = $script:Text[$script:index]

        # Razor Comment
        if ($character -eq '@' -and $script:index + 1 -lt $script:length -and $script:Text[$script:index + 1] -eq '*') {
            $closeIndex = $script:Text.IndexOf('*@', $script:index + 2)
            if ($closeIndex -lt 0) { break }
            Step ($closeIndex + 2 - $script:index)
            continue
        }

        # Html Comment
        if ($character -eq '<' -and $script:index + 3 -lt $script:length -and $script:Text.Substring($script:index, 4) -eq '<!--') {
            $closeIndex = $script:Text.IndexOf('-->', $script:index + 4)
            if ($closeIndex -lt 0) { break }
            Step ($closeIndex + 3 - $script:index)
            continue
        }

        # Opening Tag
        if ($character -eq '<' -and $script:index + 1 -lt $script:length -and $script:Text[$script:index + 1] -match '[A-Za-z]') {
            $tagLine = $script:line
            $tagColumn = $script:column
            Step 1

            $nameStart = $script:index
            while ($script:index -lt $script:length -and $script:Text[$script:index] -match '[A-Za-z0-9_.\-]') { Step 1 }
            $tagName = $script:Text.Substring($nameStart, $script:index - $nameStart)

            $attributes = New-Object System.Collections.Generic.List[object]
            $selfClosing = $false
            $closeLine = $tagLine
            $closeColumn = $tagColumn

            while ($script:index -lt $script:length) {
                # Skip Whitespace
                while ($script:index -lt $script:length -and $script:Text[$script:index] -match '\s') { Step 1 }
                if ($script:index -ge $script:length) { break }

                $current = $script:Text[$script:index]

                if ($current -eq '/' -and $script:index + 1 -lt $script:length -and $script:Text[$script:index + 1] -eq '>') {
                    $selfClosing = $true
                    $closeLine = $script:line
                    $closeColumn = $script:column
                    Step 2
                    break
                }
                if ($current -eq '>') {
                    $closeLine = $script:line
                    $closeColumn = $script:column
                    Step 1
                    break
                }

                # Attribute Name, May Start With @ Or Contain : And -
                $attributeLine = $script:line
                $attributeColumn = $script:column
                $attributeStart = $script:index
                while ($script:index -lt $script:length -and $script:Text[$script:index] -match '[@A-Za-z0-9_:.\-]') { Step 1 }
                if ($script:index -eq $attributeStart) {
                    # Nothing Consumed, Avoid An Infinite Loop
                    Step 1
                    continue
                }
                $attributeName = $script:Text.Substring($attributeStart, $script:index - $attributeStart)

                # Optional Value
                while ($script:index -lt $script:length -and $script:Text[$script:index] -match '[ \t]') { Step 1 }
                if ($script:index -lt $script:length -and $script:Text[$script:index] -eq '=') {
                    Step 1
                    while ($script:index -lt $script:length -and $script:Text[$script:index] -match '[ \t]') { Step 1 }

                    if ($script:index -lt $script:length -and ($script:Text[$script:index] -eq '"' -or $script:Text[$script:index] -eq "'")) {
                        $quote = $script:Text[$script:index]
                        Step 1
                        $depth = 0
                        while ($script:index -lt $script:length) {
                            $valueCharacter = $script:Text[$script:index]
                            if ($valueCharacter -eq '(' -or $valueCharacter -eq '[') { $depth++ }
                            elseif ($valueCharacter -eq ')' -or $valueCharacter -eq ']') { if ($depth -gt 0) { $depth-- } }
                            elseif ($valueCharacter -eq $quote -and $depth -eq 0) { Step 1; break }
                            Step 1
                        }
                    }
                    else {
                        # Unquoted Value, Ends At Whitespace Or > Outside Brackets
                        $depth = 0
                        while ($script:index -lt $script:length) {
                            $valueCharacter = $script:Text[$script:index]
                            if ($valueCharacter -eq '(' -or $valueCharacter -eq '[') { $depth++ }
                            elseif ($valueCharacter -eq ')' -or $valueCharacter -eq ']') { if ($depth -gt 0) { $depth-- } }
                            elseif ($depth -eq 0 -and ($valueCharacter -match '\s' -or $valueCharacter -eq '>')) { break }
                            elseif ($valueCharacter -eq '"' -or $valueCharacter -eq "'") {
                                $innerQuote = $valueCharacter
                                Step 1
                                while ($script:index -lt $script:length -and $script:Text[$script:index] -ne $innerQuote) { Step 1 }
                            }
                            Step 1
                        }
                    }
                }

                $attributes.Add(
                    [pscustomobject]@{
                        Name   = $attributeName
                        Line   = $attributeLine
                        Column = $attributeColumn
                    }
                )
            }

            $tags.Add(
                [pscustomobject]@{
                    Name        = $tagName
                    Line        = $tagLine
                    Column      = $tagColumn
                    Attributes  = $attributes
                    SelfClosing = $selfClosing
                    CloseLine   = $closeLine
                    CloseColumn = $closeColumn
                }
            )
            continue
        }

        Step 1
    }

    return $tags
}

$repositoryRoot = $PSScriptRoot
$resolvedPath = if ([System.IO.Path]::IsPathRooted($Path)) { $Path } else { Join-Path $repositoryRoot $Path }
if (-not (Test-Path $resolvedPath)) { throw "Path not found: $resolvedPath" }

$razorFiles = Get-ChildItem -Path $resolvedPath -Filter *.razor -Recurse -File |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' }

foreach ($razorFile in $razorFiles) {
    $relativePath = $razorFile.FullName.Replace($repositoryRoot + '\', '')
    $text = [System.IO.File]::ReadAllText($razorFile.FullName)
    $lines = $text -split "`r?`n"

    # Code Block While A Code-Behind Exists
    if ((Test-Path ($razorFile.FullName + '.cs')) -and $text -match '(?m)^\s*@code\b') {
        $codeLine = ($text.Substring(0, $text.IndexOf('@code')) -split "`n").Count
        Add-Violation $relativePath $codeLine 'CodeBlock' 'Code-behind exists, move the @code block into it'
    }

    # Final Newline
    if ($lines.Count -gt 0 -and $lines[$lines.Count - 1] -ne '') {
        Add-Violation $relativePath $lines.Count 'FinalNewline' 'File does not end with a newline'
    }

    # Raw Div And Html Comment
    for ($lineIndex = 0; $lineIndex -lt $lines.Count; $lineIndex++) {
        $lineText = $lines[$lineIndex]
        if ($lineText -match '<div[\s>/]') {
            Add-Violation $relativePath ($lineIndex + 1) 'RawDiv' 'Raw <div>, use a MudBlazor component or MudElement'
        }
        if ($lineText -match '<!--') {
            Add-Violation $relativePath ($lineIndex + 1) 'HtmlComment' 'Html comment ships to the browser, use @* *@'
        }
        if ($lineText -match '\t') {
            Add-Violation $relativePath ($lineIndex + 1) 'Tab' 'Tab character, indent with spaces'
        }
        if ($lineText -match '[ \t]$') {
            Add-Violation $relativePath ($lineIndex + 1) 'TrailingWhitespace' 'Line ends with whitespace'
        }

        # Tag Lines Indent In Steps Of Four
        if ($lineText -match '^(?<indent>[ ]*)<') {
            $indentWidth = $Matches['indent'].Length
            if ($indentWidth % 4 -ne 0) {
                Add-Violation $relativePath ($lineIndex + 1) 'Indentation' "Tag indented $indentWidth spaces, expected a multiple of 4"
            }
        }
    }

    # Razor Comment Text
    foreach ($match in [regex]::Matches($text, '@\*(.*?)\*@', 'Singleline')) {
        $commentLine = ($text.Substring(0, $match.Index) -split "`n").Count
        $commentText = $match.Groups[1].Value.Trim()
        if ($commentText -eq '') { continue }
        if ($match.Groups[1].Value -match "`n") {
            Add-Violation $relativePath $commentLine 'CommentMultiline' "Multi-line razor comment: $commentText"
            continue
        }
        if ($commentText -match '[.,;:!?]$') {
            Add-Violation $relativePath $commentLine 'CommentPunctuation' "Comment ends with punctuation: $commentText"
        }
        $words = @($commentText -split '\s+' | Where-Object { $_ -ne '' })
        if ($words.Count -gt 5) {
            Add-Violation $relativePath $commentLine 'CommentTooLong' "Comment is $($words.Count) words, name the component only: $commentText"
        }
        foreach ($word in $words) {
            $stripped = $word -replace '^[^\p{L}0-9]+', ''
            if ($stripped -ne '' -and [char]::IsLower($stripped[0])) {
                Add-Violation $relativePath $commentLine 'CommentCase' "Comment is not Title Case: $commentText"
                break
            }
        }
    }

    $tags = Get-RazorTags -Text $text

    foreach ($tag in $tags) {
        $attributes = $tag.Attributes

        if ($attributes.Count -ge 2) {
            # Every Attribute After The First Gets Its Own Line
            for ($attributeIndex = 1; $attributeIndex -lt $attributes.Count; $attributeIndex++) {
                $previous = $attributes[$attributeIndex - 1]
                $current = $attributes[$attributeIndex]
                if ($current.Line -eq $previous.Line) {
                    Add-Violation $relativePath $current.Line 'AttributePerLine' "<$($tag.Name)> attribute '$($current.Name)' shares a line with '$($previous.Name)'"
                }
                elseif ($current.Column -ne $attributes[0].Column) {
                    Add-Violation $relativePath $current.Line 'Alignment' "<$($tag.Name)> attribute '$($current.Name)' is at column $($current.Column), expected $($attributes[0].Column)"
                }
            }
        }

        # Multi-Line Open Tag Puts Content On Its Own Line
        $isMultiLineOpenTag = $tag.CloseLine -gt $tag.Line
        if ($isMultiLineOpenTag -and -not $tag.SelfClosing) {
            $closeLineText = $lines[$tag.CloseLine - 1]
            $afterCloseBracket = $closeLineText.Substring([Math]::Min($tag.CloseColumn, $closeLineText.Length))
            if ($afterCloseBracket.Trim() -ne '') {
                Add-Violation $relativePath $tag.CloseLine 'ContentOnOwnLine' "<$($tag.Name)> opens across lines, content must start on the next line"
            }
        }

        # Tooltip Takes The Comment Above The Wrapper
        if (-not $tag.SelfClosing -and $tag.Name -eq 'Tooltip') {
            $closeLineText = $lines[$tag.CloseLine - 1]
            $firstContent = $closeLineText.Substring([Math]::Min($tag.CloseColumn, $closeLineText.Length)).Trim()
            $scanIndex = $tag.CloseLine
            while ($firstContent -eq '' -and $scanIndex -lt $lines.Count) {
                $firstContent = $lines[$scanIndex].Trim()
                $scanIndex++
            }
            if ($firstContent -like '@`**') {
                Add-Violation $relativePath $scanIndex 'CommentInsideTooltip' 'Comment sits inside <Tooltip>, move it above the wrapper'
            }
        }

        # Big Containers Get One Blank Line Inside Each End
        if (-not $tag.SelfClosing -and $BigContainerTags -contains $tag.Name) {
            $afterOpenIndex = $tag.CloseLine
            if ($afterOpenIndex -lt $lines.Count -and $lines[$afterOpenIndex].Trim() -ne '') {
                Add-Violation $relativePath $tag.CloseLine 'ContainerBlankLine' "<$($tag.Name)> needs a blank line after the opening tag"
            }
            $closingTagIndex = -1
            for ($searchIndex = $tag.CloseLine; $searchIndex -lt $lines.Count; $searchIndex++) {
                if ($lines[$searchIndex] -match "</$([regex]::Escape($tag.Name))>") { $closingTagIndex = $searchIndex; break }
            }
            if ($closingTagIndex -gt 0 -and $lines[$closingTagIndex - 1].Trim() -ne '') {
                Add-Violation $relativePath ($closingTagIndex + 1) 'ContainerBlankLine' "<$($tag.Name)> needs a blank line before the closing tag"
            }
        }
    }
}

$sorted = $violations | Sort-Object File, Line

if (-not $Quiet) {
    foreach ($violation in $sorted) {
        Write-Output ("{0}({1}): {2}: {3}" -f $violation.File, $violation.Line, $violation.Rule, $violation.Message)
    }
    Write-Output ''
    Write-Output ("Checked {0} razor file(s)" -f $razorFiles.Count)
    if ($sorted.Count -eq 0) {
        Write-Output 'No violations'
    }
    else {
        Write-Output ("{0} violation(s)" -f $sorted.Count)
        $sorted | Group-Object Rule | Sort-Object Count -Descending | ForEach-Object {
            Write-Output ("  {0,4}  {1}" -f $_.Count, $_.Name)
        }
    }
}

if ($sorted.Count -gt 0) { exit 1 }
exit 0
