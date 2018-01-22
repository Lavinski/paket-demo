// include Fake libs
#r "./packages/build/FAKE/tools/FakeLib.dll"
#r "./packages/build/Microsoft.Web.Xdt/lib/net40/Microsoft.Web.XmlTransform.dll"
#r "./packages/build/FSharp.Data/lib/net45/FSharp.Data.dll"

open Fake
open Fake.FileUtils
open Fake.ArchiveHelper
open Fake.EnvironmentHelper
open Fake.AssemblyInfoFile
open Fake.AppVeyor
open Fake.UnitTestHelper
open System
open System.IO
open FSharp.Data
open Fake.Testing

(*
    paket.template
    package.template
*)

traceImportant "Started Build"

let currentDir = __SOURCE_DIRECTORY__
System.Environment.CurrentDirectory <- currentDir

// Settings //

let appVeyorVersion = AppVeyorEnvironment.BuildVersion
let isAppVeyor = not (String.IsNullOrEmpty(AppVeyorEnvironment.AccountName))
let octopusServerApiUrl = environVarOrDefault "OCTOPUS_API_URL" "" 
let octopusServerApiKey = environVarOrDefault "OCTOPUS_API_KEY" ""

// Build Parameters //

let solution = getBuildParamOrDefault "solution" "All"
let octopusProjectName = getBuildParamOrDefault "octoproj" ""
let octoPath = getBuildParamOrDefault "octo" "octo.exe"
let buildConfig = getBuildParamOrDefault "config" "Debug"
let project = getBuildParamOrDefault "project" "All"
let nugetApiKey = getBuildParamOrDefault "nugetApiKey" "XXXXXX"

// Static Settings //

// Directories
let artifacts = "./artifacts"
let deploymentArtifacts = "./deployment-artifacts"
let testArtifacts = "./test-artifacts"

// Projects
let appReferences  =
    !! "./src/**/*.csproj"
    ++ "./src/**/*.fsproj"

let solutions = 
    !! "./src/*.sln"
    
// FindProjects
let projectsToNugetTemplates =
    !! "./src/**/paket.template"

let projectsToOctoPackTemplates =
    !! "./src/**/octopus.template"

let executables =
    !! (sprintf "./src/**/bin/%s/*.exe" buildConfig)

type ProjectType = 
    | Nuget
    | Octopus

type Project = {
    Name: string;
    PackageName: string;
    Directory: System.IO.DirectoryInfo;
    Version: string;
    TemplatePath: string
    ProjectType: ProjectType
}

let getFieldFromTemplate fieldName templatePath = 
    StringHelper.ReadFile templatePath
    |> Seq.where (fun line -> line.StartsWith(fieldName))
    |> Seq.map (fun line -> line.Replace(fieldName, "").Trim())
    |> Seq.tryFind(fun x -> true)

let getVersionFromTemplate templatePath = 
    getFieldFromTemplate "Version" templatePath

let getNameFromTemplate templatePath = 
    getFieldFromTemplate "Name" templatePath

let getVersionWithFallbacks projectDir templatePath (projectType: ProjectType) =
    match projectType with
    | Nuget ->
        match getVersionFromTemplate templatePath with
            | Some(x) -> x
            | None -> 
                if String.IsNullOrEmpty(appVeyorVersion) then
                    "0.0.1"
                else
                    appVeyorVersion
    | Octopus ->
        if String.IsNullOrEmpty(appVeyorVersion) then 
            let versionFile = (currentDir + ".\\.version")
            if File.Exists(versionFile) then
                StringHelper.ReadFile versionFile |> Seq.head
            else
                "0.0.1"
        else
            appVeyorVersion

let getProjects projectsToPackage (projectType: ProjectType) =
    projectsToPackage
    |> Seq.map (fun templatePath -> 
        let dir = (directoryInfo templatePath).Parent
        {
            Name = dir.Name;
            PackageName = 
                match projectType with
                | Nuget -> dir.Name
                | Octopus -> (getNameFromTemplate templatePath).Value;
            Directory = dir;
            Version = getVersionWithFallbacks dir.FullName templatePath projectType;
            TemplatePath = templatePath
            ProjectType = projectType
        })
    
let projectsToNuget = getProjects projectsToNugetTemplates Nuget
let projectsToOctoPack = getProjects projectsToOctoPackTemplates Octopus
let projects = Seq.append projectsToNuget projectsToOctoPack

// MSBuild
MSBuildDefaults <- {
    MSBuildDefaults with
        ToolsVersion = None
        Verbosity = Some MSBuildVerbosity.Minimal }

let transformConfig (xmlFilePath:string) (transformationFilePath:string) (outputFilePath:string) =
    use targetDocument = new Microsoft.Web.XmlTransform.XmlTransformableDocument()
    targetDocument.PreserveWhitespace <- true

    use xmlFile = new FileStream(xmlFilePath, FileMode.Open, FileAccess.Read)
    targetDocument.Load(xmlFile);

    use transformFile = new FileStream(transformationFilePath, FileMode.Open, FileAccess.Read)
    use transform = new Microsoft.Web.XmlTransform.XmlTransformation(transformFile, null)
    transform.Apply(targetDocument) |> ignore

    try
        use fileStream = new FileStream(outputFilePath, FileMode.Create, FileAccess.ReadWrite)
        use streamWriter = new StreamWriter(fileStream)
        targetDocument.PreserveWhitespace <- false
        targetDocument.Save(streamWriter)
    with
    | :? IOException as ex when ex.Message.StartsWith("The process cannot access the file") -> 
        failwith "Cannot write to target file"

let tryNugetRestore () = 
    logfn "Trying to run NuGet package restore"
    let srcPath = Path.GetFullPath(Path.Combine(currentDir, "src"))
    let nugetPath = ".nuget\\NuGet.exe"
    let combinedPath = Path.Combine(srcPath, nugetPath)
    let doesNuGetExist = File.Exists(combinedPath)
    if doesNuGetExist then
        logfn "Nuget found, running restore"
        execProcess (fun si ->
            si.WorkingDirectory <- srcPath
            si.FileName <- nugetPath
            si.Arguments <- "restore"
            si.UseShellExecute <- true
            ) (TimeSpan.FromMinutes(2.0)) |> ignore
    else
        logfn "Nuget not found, skipping restore"
    doesNuGetExist

let packageNugetProjects () = 
    let paketPath = Path.GetFullPath(Path.Combine(currentDir, ".paket/paket.exe"))
    logfn "Paket Path: %s" paketPath
    CreateDir artifacts
    for project in projectsToNuget do
        logfn "Processing project %s version %s " project.Name project.Version |> ignore
        let name = project.Name.ToLowerInvariant()
        let version = project.Version
                
        Paket.Pack (fun p ->
            { p with
                ToolPath = paketPath
                WorkingDir = project.Directory.FullName
                OutputPath = "../../artifacts"
                TemplateFile = project.TemplatePath
                BuildConfig = buildConfig
                BuildPlatform = "AnyCPU"
                MinimumFromLockFile = true
                IncludeReferencedProjects = true
                //Symbols = true
                
            }
        )

let octoPack packageName version projectPath = 
    let buildDir = (sprintf "%s/bin/%s/" projectPath buildConfig)
    let suffix = if buildConfig = "Release" then "" else "-" + buildConfig
    let exec = "pack --format Zip --overwrite --id " + packageName + suffix + " --version " + version + " --basePath " + buildDir + " --outFolder " + deploymentArtifacts
    logfn "Running: %s" exec
    let result = Shell.Exec(octoPath, exec)
    if result <> 0
    then failwithf "%s exited with error %d" "octo.exe" result

let packageDeploymentProjects () = 
    CreateDir deploymentArtifacts
    // OctoPack the other projects!
    for project in projectsToOctoPack do
        octoPack project.PackageName project.Version project.Directory.FullName

let octoPush deploymentArtifact = 
    if (String.IsNullOrEmpty(octopusServerApiUrl)) 
    then failwith "The Octopus server API URL has not been supplied in the environment variable 'OCTOPUS_API_URL'"
    if (String.IsNullOrEmpty(octopusServerApiKey)) 
    then failwith "The Octopus server API key has not been supplied in the environment variable 'OCTOPUS_API_KEY'"

    let exec = "push --package " + deploymentArtifact + " --server " + octopusServerApiUrl + " --apiKey " + octopusServerApiKey
    logfn "Running: %s" exec
    let result = Shell.Exec(octoPath, exec)
    if result <> 0 then failwithf "%s exited with error %d" "octo.exe" result

let octoCreateRelease projectName = 
    if (String.IsNullOrEmpty(projectName)) then failwith "Project name not supplied"
    let exec = sprintf "create-release --project \"%s\" --server %s --apiKey %s" projectName octopusServerApiUrl octopusServerApiKey
    logfn "Running: %s" exec
    let result = Shell.Exec(octoPath, exec)
    if result <> 0 then failwithf "%s exited with error %d" "octo.exe" result

let pushDeploymentProjects () = 
    let deploymentArtifacts =
        !! (deploymentArtifacts + "\\*.zip")

    for deploymentArtifact in deploymentArtifacts do
        octoPush deploymentArtifact

let makeDepeloperConfig () =
    let configTempaltes =
        !! "./src/**/*.config.template"
    
    logfn "Making developer config for Project(s) %s" project
    for template in configTempaltes |> Seq.filter (fun x -> x.StartsWith(project) || project = "All") do
        let path = Path.GetDirectoryName(template)
        let newFileName = Path.GetFileNameWithoutExtension(template)
        let newFilePath = path @@ newFileName
        if not (File.Exists newFilePath) then
            logfn "Creating new file from config template: %s" newFileName
            File.Copy(template, newFilePath)
        else
            logfn "Config '%s' already exists, don't need to create it" newFileName

type ConfigType = 
    | WebConfig
    | AppConfig

let typeName configType = 
    match configType with
    | WebConfig -> "Web"
    | AppConfig -> "App"

let transformBuildConfig () =
    traceImportant "Config Transforms"

    let configTransformTargets =
        !! "./src/*/App.config"
        ++ "./src/*/Web.config"

    let transformConfigName = if buildConfig = "Debug" then "Developer" else buildConfig

    logfn "Transforming build config for Project(s) %s" project
    for configTransformTarget in configTransformTargets |> Seq.filter (fun x -> x.StartsWith(project) || project = "All") do
        let project = (directoryInfo configTransformTarget).Parent.Name
        let projectPath = (directoryInfo configTransformTarget).Parent.FullName
        let fileName = Path.GetFileName(configTransformTarget)

        let configType = if String.Compare(fileName, "web.config", true) = 0 then WebConfig else AppConfig

        printfn "Runing config transforms for %s" project

        //let projectName = Path.GetDirectoryName(project)

        let transformFile = sprintf "%s.%s.config" (typeName configType) transformConfigName
        let transformFilePath = Path.Combine(projectPath, transformFile)
        if File.Exists(transformFilePath) then
            logfn "Transforming %s with %s" fileName transformFile

            let runtimeConfigFilePath =
                match configType with
                | AppConfig -> (!! (sprintf @"%s\bin\%s\%s.exe.config" projectPath buildConfig project)) |> Seq.head
                | WebConfig -> (sprintf @"%s\bin\%s" projectPath fileName)

            logfn "Runtime config file path: %s" runtimeConfigFilePath
            let runtimeConfigFilePath = fileInfo runtimeConfigFilePath

            let configTemplate = runtimeConfigFilePath.FullName + ".xml"
            File.Copy(runtimeConfigFilePath.FullName, configTemplate, true)
            logfn "Transforming %s" configTemplate
            logfn "With %s" transformFilePath
            logfn "Into %s" runtimeConfigFilePath.FullName
            transformConfig configTemplate transformFilePath runtimeConfigFilePath.FullName
            printfn "Config transform complete"
        else
            printfn "No transform file for %s found" transformFilePath

// Targets

Target "Clean" (fun _ ->
    CleanDirs [deploymentArtifacts; artifacts]
)

Target "Version" (fun _ ->
    logfn "Versioning all projects"
    for project in projects do  
        logfn "Applying version to project %s" project.Name
        CreateFSharpAssemblyInfo (project.Directory.FullName @@ "AssemblyVersionInfo.fs")
            [
                Attribute.Version project.Version;
                Attribute.FileVersion project.Version
            ]
)

Target "MsBuild" (fun _ ->
    tryNugetRestore () |> ignore
    logfn "Running MSBuild"
    
    //for solution in solutions do //appReferences
    MSBuild null "Build" ["Configuration", buildConfig; "InsideFake", "true"] solutions |> Log "AppBuild-Output: "
)

Target "Config" (fun _ ->
    makeDepeloperConfig ()
    transformBuildConfig ()
)

Target "Build" (fun _ ->
    logfn "Building Everything"
)

Target "Test" (fun _ ->  
    !! (sprintf "./src/**/bin/%s/*Tests.dll" buildConfig)
    ++ (sprintf "./src/**/bin/%s/*Tests.exe" buildConfig)
    |> xUnit2 (fun p -> { p with HtmlOutputPath = Some (testArtifacts @@ "xunit.html") })
)

Target "Package-Nuget-Single" (fun _ ->
    packageNugetProjects ()
)


Target "Package-Nuget" (fun _ ->
    packageNugetProjects ()
)

Target "Package-Deployables" (fun _ ->
    packageDeploymentProjects ()
)

Target "Package" (fun _ ->
    ()
)

Target "Publish" (fun _ ->
    pushDeploymentProjects ()
    if not <| String.IsNullOrWhiteSpace(octopusProjectName) then
        octoCreateRelease octopusProjectName
)

Target "Run" (fun _ ->
    for executable in executables do
        logfn "Running: %s" executable
        System.Diagnostics.Process.Start(executable, "") |> ignore
)

// Build order
"Clean"   ?=> "MsBuild"
"MsBuild" <== [ "Version" ]
"Config"  <=? "MsBuild"
"Build"   <== [ "MsBuild"; "Config" ]
"Test"    <== [ "Build" ]
"Package-Nuget" <== [ "Clean"; "Build"; ]
"Package-Deployables" <== [ "Clean"; "Build"; ]
"Package" <== [ "Package-Nuget"; "Package-Deployables"; ]
"Publish" <== [ "Package" ]
"Run"     <== [ "Test" ]

RunTargetOrDefault "Package"