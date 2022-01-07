﻿namespace ArcCommander.APIs

open System
open System.IO

open ArcCommander
open ArcCommander.ArgumentProcessing

open ISADotNet
open ISADotNet.XLSX

/// ArcCommander API functions that get executed by top level subcommand verbs
module ArcAPI = 

    // TODO TO-DO TO DO: make use of args
    /// Initializes the arc specific folder structure
    let init (arcConfiguration : ArcConfiguration) (arcArgs : Map<string,Argument>) =

        let log = Logging.createLogger "ArcInitLog"

        let verbosity = GeneralConfiguration.getVerbosity arcConfiguration
        
        log.Info("Start Arc Init")

        let workDir = GeneralConfiguration.getWorkDirectory arcConfiguration

        let editor =            tryGetFieldValueByName "EditorPath" arcArgs
        let gitLFSThreshold =   tryGetFieldValueByName "GitLFSByteThreshold" arcArgs
        let repositoryAdress =  tryGetFieldValueByName "RepositoryAdress" arcArgs 


        log.Trace("Create Directory")

        Directory.CreateDirectory workDir |> ignore

        log.Trace("Initiate folder structure")

        ArcConfiguration.getRootFolderPaths arcConfiguration
        |> Array.iter (Directory.CreateDirectory >> ignore)

        log.Trace("Set configuration")

        match editor with
        | Some editorValue -> 
            let path = IniData.getLocalConfigPath workDir
            IniData.setValueInIniPath path "general.editor" editorValue
        | None -> ()

        match gitLFSThreshold with
        | Some gitLFSThresholdValue -> 
            let path = IniData.getLocalConfigPath workDir
            IniData.setValueInIniPath path "general.gitlfsbytethreshold" gitLFSThresholdValue
        | None -> ()

        log.Trace("Init Git repository")

        try

            Fake.Tools.Git.Repository.init workDir false true

            match repositoryAdress with
            | None -> ()
            | Some remote ->
                GitAPI.executeGitCommand workDir ("remote add origin " + remote) |> ignore

        with 
        | _ -> 

            log.Error("ERROR: Git could not be set up. Please try installing Git cli and run `arc git init`.")

    /// Update the investigation file with the information from the other files and folders.
    let update (arcConfiguration : ArcConfiguration) =

        let log = Logging.createLogger "ArcUpdateLog"
        
        log.Info("Start Arc Update")

        let assayRootFolder = AssayConfiguration.getRootFolderPath arcConfiguration

        let investigationFilePath = IsaModelConfiguration.getInvestigationFilePath arcConfiguration

        let assayNames = 
            DirectoryInfo(assayRootFolder).GetDirectories()
            |> Array.map (fun d -> d.Name)
            
        let investigation =
            try Investigation.fromFile investigationFilePath 
            with
            | :? FileNotFoundException -> 
                Investigation.empty
            | err -> raise err

        let rec updateInvestigationAssays (assayNames : string list) (investigation : Investigation) =
            match assayNames with
            | a :: t ->
                let assayFilePath = IsaModelConfiguration.getAssayFilePath a arcConfiguration
                let assayFileName = IsaModelConfiguration.getAssayFileName a arcConfiguration
                let factors,protocols,persons,assay = AssayFile.Assay.fromFile assayFilePath
                let studies = investigation.Studies

                match studies with
                | Some studies ->
                    match studies |> Seq.tryFind (API.Study.getAssays >> Option.defaultValue [] >> API.Assay.existsByFileName assayFileName) with
                    | Some study -> 
                        study
                        |> API.Study.mapAssays (API.Assay.updateByFileName API.Update.UpdateByExistingAppendLists assay)
                        |> API.Study.mapFactors (List.append factors >> List.distinctBy (fun f -> f.Name))
                        |> API.Study.mapProtocols (List.append protocols >> List.distinctBy (fun p -> p.Name))
                        |> API.Study.mapContacts (List.append persons >> List.distinctBy (fun p -> p.FirstName,p.LastName))
                        |> fun s -> API.Study.updateBy ((=) study) API.Update.UpdateAll s studies
                    | None ->
                        Study.fromParts (Study.StudyInfo.create a "" "" "" "" "" []) [] [] factors [assay] protocols persons
                        |> API.Study.add studies
                | None ->                   
                    [Study.fromParts (Study.StudyInfo.create a "" "" "" "" "" []) [] [] factors [assay] protocols persons]
                |> API.Investigation.setStudies investigation
                |> updateInvestigationAssays t
            | [] -> investigation

        updateInvestigationAssays (assayNames |> List.ofArray) investigation
        |> Investigation.toFile investigationFilePath

    /// Export the complete ARC as a JSON object.
    let export (arcConfiguration : ArcConfiguration) (arcArgs : Map<string,Argument>) =
    
        let log = Logging.createLogger "ArcExportLog"

        log.Info("Start Arc Export")
    
        let assayRootFolder = AssayConfiguration.getRootFolderPath arcConfiguration
    
        let investigationFilePath = IsaModelConfiguration.getInvestigationFilePath arcConfiguration
    
        let assayNames = 
            DirectoryInfo(assayRootFolder).GetDirectories()
            |> Array.map (fun d -> d.Name)
                
        let investigation =
            try Investigation.fromFile investigationFilePath 
            with
            | :? FileNotFoundException -> 
                Investigation.empty
            | err -> raise err
    
        let rec updateInvestigationAssays (assayNames : string list) (investigation : Investigation) =
            match assayNames with
            | a :: t ->
                let assayFilePath = IsaModelConfiguration.getAssayFilePath a arcConfiguration
                let assayFileName = (IsaModelConfiguration.getAssayFileName a arcConfiguration).Replace("\\","/")
                let factors,protocols,persons,assay = AssayFile.Assay.fromFile assayFilePath
                let studies = investigation.Studies

                match studies with
                | Some studies ->
                    match studies |> Seq.tryFind (API.Study.getAssays >> Option.defaultValue [] >> API.Assay.existsByFileName assayFileName) with
                    | Some study -> 
                        study
                        |> API.Study.mapAssays (API.Assay.updateByFileName API.Update.UpdateByExistingAppendLists assay)
                        |> API.Study.mapFactors (List.append factors >> List.distinctBy (fun f -> f.Name))
                        |> API.Study.mapProtocols (List.append protocols >> List.distinctBy (fun p -> p.Name))
                        |> API.Study.mapContacts (List.append persons >> List.distinctBy (fun p -> p.FirstName,p.LastName))
                        |> fun s -> API.Study.updateBy ((=) study) API.Update.UpdateAll s studies
                    | None ->
                        Study.fromParts (Study.StudyInfo.create a "" "" "" "" "" []) [] [] factors [assay] protocols persons
                        |> API.Study.add studies
                | None ->                   
                    [Study.fromParts (Study.StudyInfo.create a "" "" "" "" "" []) [] [] factors [assay] protocols persons]
                |> API.Investigation.setStudies investigation
                |> updateInvestigationAssays t
            | [] -> investigation
                
        let output = updateInvestigationAssays (assayNames |> List.ofArray) investigation

        if containsFlag "ProcessSequence" arcArgs then

            let output = 
                output.Studies 
                |> Option.defaultValue [] |> List.collect (fun s -> 
                    s.Assays
                    |> Option.defaultValue [] |> List.collect (fun a -> 
                        a.ProcessSequence |> Option.defaultValue []
                    )
                )

            match tryGetFieldValueByName "Path" arcArgs with
            | Some p -> ArgumentProcessing.serializeToFile p output
            | None -> ()

            //System.Console.Write(ArgumentProcessing.serializeToString output)
            log.Debug(ArgumentProcessing.serializeToString output)
        else 
               
            match tryGetFieldValueByName "Path" arcArgs with
            | Some p -> ISADotNet.Json.Investigation.toFile p output
            | None -> ()

            //System.Console.Write(ISADotNet.Json.Investigation.toString output)
            log.Debug(ISADotNet.Json.Investigation.toString output) // check if converter is fine with this...

    /// Deletes every ARC-related file and folder that can be found from arcConfigguration.
    let delete (arcConfiguration : ArcConfiguration) =

        let log = Logging.createLogger "ArcDeleteLog"

        log.Warn("WARNING: Deleting the ARC results in permanent loss of all files and folders related to this ARC. They can only be recovered if you have a copy elsewhere or already committed the ARC to a repository. Do you want to proceed? (y/n)")

        let rec matchInput input =
            match input with
            | "y" ->
                log.Info("Start Arc Delete")
        
                let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
               
                let investigation = Investigation.fromFile investigationFilePath
        
                let identifier = getFieldValueByName "Identifier" investigationArgs
                
                if Some identifier = investigation.Identifier then
                    System.IO.File.Delete investigationFilePath

            | "n" -> log.Trace("Arc Delete aborted")

            | _ -> 
                log.Debug("Inproper input given. Choose between \"y\" (yes) and \"n\" (no).")
                let newInput = Console.ReadLine()
                matchInput newInput

        let input = Console.ReadLine()

        matchInput input

    /// Returns true if called anywhere in an ARC.
    let isArc (arcConfiguration : ArcConfiguration) (arcArgs : Map<string,Argument>) = raise (NotImplementedException())