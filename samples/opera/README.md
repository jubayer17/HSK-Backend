# Private Opera acceptance files

Put `task_sheet_report_1914628800.PDF` in this folder. The exact October file was not supplied during the code update.
The four available July/August reports were copied here for local regression testing. PDFs stay ignored by Git because they contain staff data.

The final acceptance gate remains **pending** until the October PDF and an independently reviewed
`task_sheet_report_1914628800.expected.json` are present. The expected JSON has a `sha256` string
and a `rooms` array containing every field of each `ParsedRoom`, including its `position` object.
Do not generate expected values by trusting the parser's own output; check them against the PDF.

Run `powershell -File tools/Test-FinalSample.ps1` from the backend root. This strict command fails
when either file is missing. Ordinary regression runs explicitly skip the unavailable acceptance test.

The decision repeats 28 rooms on page 2 + 28 on page 4 = 56, with summary 109. One action-plan line
says 84. The strict test uses the repeated 56-room requirement; confirm it against the real file.
