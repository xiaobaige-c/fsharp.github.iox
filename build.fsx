// --------------------------------------------------------------------------------------
// FAKE build script that builds the web site & runs it in a local HTTP server
// --------------------------------------------------------------------------------------

#r "packages/FAKE/tools/FakeLib.dll"
#I "packages/RazorEngine.3.3.0/lib/net40"
#I "packages/Microsoft.AspNet.Razor.2.0.30506.0/lib/net40"
#I "packages/FSharp.Formatting.2.4.8/lib/net40"
#I "packages/FSharp.Compiler.Service.0.0.44/lib/net40"

#r "RazorEngine.dll"
#r "FSharp.Literate.dll"
#r "FSharp.CodeFormat.dll"
#load "packages/HttpServer.fs"

open System
open System.IO
open System.Collections.Generic
open System.Text.RegularExpressions
open Fake 
open FSharp.Literate
open FSharp.Http

// --------------------------------------------------------------------------------------
// Configuration
// --------------------------------------------------------------------------------------

/// Output directory for the generated site
let site = "_site"

/// Port for the HTTP server to start
let port = 8080

/// Directories that should be copied to the output directory
let copy = [ "css"; "img" ]

/// Directories and files that should be skipped (when looking
/// for source files)
let exclusions = [ "_layouts"; "packages"; "README.md"; "_site" ]

// --------------------------------------------------------------------------------------
// Implementation
// --------------------------------------------------------------------------------------

let (-/-) a b = Path.Combine(a, b)
let root = __SOURCE_DIRECTORY__
Environment.CurrentDirectory <- root 
let siteDir = root -/- site
let exclusionSet = 
  set [ for n in exclusions -> Path.Combine(root, n) ]

/// Get a sequence of all *.md and *.html files that are not excluded
let rec enumerateSiteFiles current = seq {
  let files =
    Seq.concat [ Directory.GetFiles(current, "*.md")
                 Directory.GetFiles(current, "*.html") ]
  for file in files do 
    if not (exclusionSet.Contains(file)) then yield file
  for dir in Directory.GetDirectories(current) do
    if not (exclusionSet.Contains(dir)) then 
      yield! enumerateSiteFiles dir }

/// Search for Jekyll header - file can start with "---" delimited block
/// that contains "key: value" pairs. Returns dictionary with key-value 
/// pairs, together with the body
let parseHeader (lines:string[]) = 
  if lines.[0] = "---" then
    let endIndex = lines |> Seq.skip 1 |> Seq.findIndex ((=) "---")
    let header = lines.[1 .. endIndex]
    let body = lines.[endIndex + 2 .. lines.Length - 1]
    let header =
      [ for line in header do 
          let split = line.IndexOf(':')
          let key = line.Substring(0, split).Trim()
          let value = line.Substring(split + 1, line.Length - split - 1).Trim()
          yield key, value ] |> dict
    Some(header), body |> String.concat "\n"
  else 
    None, lines |> String.concat "\n"

/// Replace "{{ page.<key> }}" in the template with the values specified by 'headers'
/// Additionally, replaces "{{ content }}" with the specified body.
let replaceKeys template body (header:IDictionary<string, string>) =
  Regex.Replace(template, "\{\{\\s*(?<key>[a-z\-\.]*)\\s*\}\}", fun (m:Match) -> 
    let key = m.Groups.["key"].Value
    if key = "content" then body
    elif key.StartsWith("page.") then 
      match header.TryGetValue(key.Substring("page.".Length)) with
      | true, value -> value
      | _ -> "" 
    else "")

/// Takes html & headers (result of 'parseHeader') and applies 
/// the template specified by the 'layout' key.
let rec applyTemplates html (header:IDictionary<string, string>) =
  let templateFile = root -/- "_layouts" -/- (header.["layout"] + ".html")
  let subheader, template = parseHeader(File.ReadAllLines(templateFile))
  let body = replaceKeys template html header
  match subheader with
  | None -> body
  | Some subheader ->
      let header =
        [ for (KeyValue(k, v)) in header do yield k, v          
          for (KeyValue(k, v)) in subheader do yield k, v ] |> dict
      applyTemplates body header

// --------------------------------------------------------------------------------------
// Generate assembly info files with the right version & up-to-date information


// Delete the '_site' folder & re-create it
Target "Clean" (fun _ ->
  CleanDirs [site]
)

// Copy all static files to the output folder
Target "Copy" (fun _ ->
  let copy() = 
    for subdir in copy do
      ensureDirectory (root -/- site -/- subdir)
      CopyRecursive (root -/- subdir) (root -/- site -/- subdir) true |> ignore
  copy()
)

// Process the site & generate the output HTML
Target "Generate" (fun _ ->
  let generate () = 
    for file in enumerateSiteFiles root do 
      // Get name for the output file
      let output = file.Replace(root, siteDir)

      // Process Markdown or HTML
      let header, html, output = 
        if file.EndsWith(".md") then
          let header, body = parseHeader(File.ReadAllLines(file))
        
          // Process the file in a temp directory & read the HTML
          let temp = Path.GetTempFileName()
          File.WriteAllText(temp, body)
          Literate.ProcessMarkdown(temp, output=temp + ".html")
          let html = File.ReadAllText(temp + ".html")
          File.Delete(temp)
          File.Delete(temp + ".html")

          // Return HTML & new output file name (.md -> .html)
          let output = output.Substring(0, output.Length - 3) + ".html"
          header, html, output
        else 
          let header, html = parseHeader(File.ReadAllLines(file))
          header, html, output

      // Apply template (if any) and write to the output    
      let outputHtml = 
        match header with 
        | None -> html
        | Some header -> applyTemplates html header

      printfn "Writing: %s" output
      ensureDirectory (Path.GetDirectoryName(output))
      File.WriteAllText(output, outputHtml)
  generate()
)

// Start HTTP server with the web site
Target "Run" (fun _ ->
  let url = sprintf "http://localhost:%d/" port
  let server = HttpServer.Start(url, root -/- site)
  printfn "Starting web server at %s" url
  System.Diagnostics.Process.Start(url) |> ignore
  System.Console.ReadLine() |> ignore
  server.Stop()
)

Target "All" DoNothing

"Clean"
  ==> "Copy"
  ==> "Generate"
  ==> "Run"
  ==> "All"

Run <| getBuildParamOrDefault "target" "All"
