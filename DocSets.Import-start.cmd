set path=d:\-Projects-\VS\DocSets\DocSets.Import\bin\Debug\net472\;%path%
DocSets.Import.exe ^
  .\DocSets.docsets.json ^
  --output .\DocSetsNew.DocSets ^
  --source-id docsets ^
  --source-name DocSets ^
  --source-root .. ^
  --solution DocSets.sln