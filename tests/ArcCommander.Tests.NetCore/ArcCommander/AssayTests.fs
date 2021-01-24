﻿module AssayTests

open Argu
open Expecto
open TestingUtils
open ISADotNet
open ArcCommander
open ArgumentProcessing
open ArcCommander.CLIArguments
open ArcCommander.APIs


let standardISAArgs = 
    Map.ofList 
        [
            "investigationfilename","isa.investigation.xlsx";
            "studiesfilename","isa.study.xlsx";
            "assayfilename","isa.assay.xlsx"
        ]

let processCommand (arcConfiguration:ArcConfiguration) commandF (r : 'T list when 'T :> IArgParserTemplate) =

    let g = groupArguments r
    Prompt.createArgumentQueryIfNecessary "" "" g 
    |> snd
    |> commandF arcConfiguration

let setupArc (arcConfiguration:ArcConfiguration) =
    let investigationArgs = [InvestigationCreateArgs.Identifier "TestInvestigation"]
    let arcArgs : ArcInitArgs list =  [] 

    processCommand arcConfiguration ArcAPI.init             arcArgs
    processCommand arcConfiguration InvestigationAPI.create investigationArgs


[<Tests>]
let testAssayTestFunction = 

    let testDirectory = __SOURCE_DIRECTORY__ + @"/TestFiles/"
    let investigationFileName = "isa.investigation.xlsx"
    let investigationFilePath = System.IO.Path.Combine(testDirectory,investigationFileName)

    testList "AssayTestFunction" [
        testCase "MatchesAssayValues" (fun () -> 
            let testAssay = ISADotNet.XLSX.Assays.fromString
                                "protein expression profiling" "http://purl.obolibrary.org/obo/OBI_0000615" "OBI"
                                "mass spectrometry" "" "OBI" "iTRAQ" "a_proteome.txt" []
            let investigation = ISADotNet.XLSX.Investigation.fromFile investigationFilePath
            // Positive control
            Expect.equal investigation.Studies.Head.Assays.Head testAssay "The assay in the file should match the one created per hand but did not"
            // Negative control
            Expect.notEqual investigation.Studies.Head.Assays.[1] testAssay "The assay in the file did not match the one created per hand and still returned true"
        )
        testCase "ListsCorrectAssaysInCorrectStudies" (fun () -> 
            let testAssays = [
                "BII-S-1",["a_proteome.txt";"a_metabolome.txt";"a_transcriptome.txt"]
                "BII-S-2",["a_microarray.txt"]
            ]
            let investigation = ISADotNet.XLSX.Investigation.fromFile investigationFilePath

            testAssays
            |> List.iter (fun (studyIdentifier,assayIdentifiers) ->
                match ISADotNet.API.Study.tryGetByIdentifier studyIdentifier investigation.Studies with
                | Some study ->
                    let actualAssayIdentifiers = study.Assays |> List.map (fun a -> a.FileName)
                    Expect.sequenceEqual actualAssayIdentifiers assayIdentifiers "Assay Filenames did not match the expected ones"
                | None -> failwith "Study %s could not be taken from the ivnestigation even though it should be there"                    
            )
        )

    ]
    |> testSequenced

[<Tests>]
let testAssayRegister = 

    let testDirectory = __SOURCE_DIRECTORY__ + @"/TestResult/assayRegisterTest"

    let configuration = 
        ArcConfiguration.create 
            (Map.ofList ["workdir",testDirectory;"verbosity","2"]) 
            (standardISAArgs)
            Map.empty Map.empty Map.empty Map.empty


    testList "AssayRegister" [

        testCase "AddToExistingStudy" (fun () -> 

            let assayIdentifier = "TestAssay"
            let assayFileName = IsaModelConfiguration.getAssayFileName assayIdentifier configuration
            let measurementType = "TestMeasurementType"
            let studyIdentifier = "TestStudy"
            
            let studyArgs : StudyRegisterArgs list = [Identifier studyIdentifier]
            let assayArgs : AssayRegisterArgs list = [AssayRegisterArgs.StudyIdentifier studyIdentifier;AssayRegisterArgs.AssayIdentifier assayIdentifier;MeasurementType measurementType]
            let testAssay = ISADotNet.XLSX.Assays.fromString measurementType "" "" "" "" "" "" assayFileName []
            
            setupArc configuration
            processCommand configuration StudyAPI.register studyArgs
            processCommand configuration AssayAPI.register assayArgs
            
            let investigation = ISADotNet.XLSX.Investigation.fromFile (IsaModelConfiguration.getInvestigationFilePath configuration)
            match API.Study.tryGetByIdentifier studyIdentifier investigation.Studies with
            | Some study ->
                let assayOption = API.Assay.tryGetByFileName assayFileName study.Assays
                Expect.isSome assayOption "Assay was not added to study"
                Expect.equal assayOption.Value testAssay "Assay is missing values"
            | None ->
                failwith "Study was not added to the investigation"
        )
        testCase "DoNothingIfAssayExisting" (fun () -> 
            let assayIdentifier = "TestAssay"
            let assayFileName = IsaModelConfiguration.getAssayFileName assayIdentifier configuration
            let measurementType = "TestMeasurementType"
            let studyIdentifier = "TestStudy"
            
            let assayArgs : AssayRegisterArgs list = [AssayRegisterArgs.StudyIdentifier studyIdentifier;AssayRegisterArgs.AssayIdentifier assayIdentifier]
            let testAssay = ISADotNet.XLSX.Assays.fromString measurementType "" "" "" "" "" "" assayFileName []
            
            processCommand configuration AssayAPI.register assayArgs
            
            let investigation = ISADotNet.XLSX.Investigation.fromFile (IsaModelConfiguration.getInvestigationFilePath configuration)
            match API.Study.tryGetByIdentifier studyIdentifier investigation.Studies with
            | Some study ->
                let assayOption = API.Assay.tryGetByFileName assayFileName study.Assays
                Expect.isSome assayOption "Assay is no longer part of study"
                Expect.equal assayOption.Value testAssay "Assay values were removed"
                Expect.equal study.Assays.Length 1 "Assay was added even though it should't have been as an assay with the same identifier was already present"
            | None ->
                failwith "Study was removed from the investigation"
        )
        testCase "AddSecondAssayToExistingStudy" (fun () -> 

            let oldAssayIdentifier = "TestAssay"
            let oldAssayFileName = IsaModelConfiguration.getAssayFileName oldAssayIdentifier configuration
            let oldmeasurementType = "TestMeasurementType"
            let studyIdentifier = "TestStudy"
            
            let newAssayIdentifier = "SecondTestAssay"
            let newAssayFileName = IsaModelConfiguration.getAssayFileName newAssayIdentifier configuration
            let newmeasurementType = "OtherMeasurementType"

            let assayArgs = [AssayRegisterArgs.StudyIdentifier studyIdentifier;AssayRegisterArgs.AssayIdentifier newAssayIdentifier;MeasurementType newmeasurementType]
            let testAssay = ISADotNet.XLSX.Assays.fromString newmeasurementType "" "" "" "" "" "" newAssayFileName []
            
            processCommand configuration AssayAPI.register assayArgs
            
            let investigation = ISADotNet.XLSX.Investigation.fromFile (IsaModelConfiguration.getInvestigationFilePath configuration)
            match API.Study.tryGetByIdentifier studyIdentifier investigation.Studies with
            | Some study ->
                let assayFileNames = study.Assays |> List.map (fun a -> a.FileName)
                let assayOption = API.Assay.tryGetByFileName newAssayFileName study.Assays
                Expect.isSome assayOption "New Assay was not added to study"
                Expect.equal assayOption.Value testAssay "New Assay is missing values"
                Expect.sequenceEqual assayFileNames [oldAssayFileName;newAssayFileName] "Either an assay is missing or they're in an incorrect order"
            | None ->
                failwith "Study was removed from the investigation"
        )
        testCase "CreateStudyIfNotExisting" (fun () -> 
            let assayIdentifier = "TestAssay"
            let assayFileName = IsaModelConfiguration.getAssayFileName assayIdentifier configuration
            let measurementType = "MeasurementTypeOfStudyCreatedByAssayRegister"
            let studyIdentifier = "StudyCreatedByAssayRegister"
            
            let assayArgs : AssayRegisterArgs list = [AssayRegisterArgs.StudyIdentifier studyIdentifier;AssayRegisterArgs.AssayIdentifier assayIdentifier;MeasurementType "MeasurementTypeOfStudyCreatedByAssayRegister"]
            let testAssay = ISADotNet.XLSX.Assays.fromString measurementType "" "" "" "" "" "" assayFileName []
            
            processCommand configuration AssayAPI.register assayArgs
            
            let investigation = ISADotNet.XLSX.Investigation.fromFile (IsaModelConfiguration.getInvestigationFilePath configuration)
            let studyOption = API.Study.tryGetByIdentifier studyIdentifier investigation.Studies
            Expect.isSome studyOption "Study should have been created in order for assay to be placed in it"     
            
            let assayOption = API.Assay.tryGetByFileName assayFileName studyOption.Value.Assays
            Expect.isSome assayOption "Assay was not added to newly created study"
            Expect.equal assayOption.Value testAssay "Assay is missing values"
        )
        testCase "StudyNameNotGivenUseAssayName" (fun () -> 
            let assayIdentifier = "TestAssayWithoutStudyName"
            let assayFileName = IsaModelConfiguration.getAssayFileName assayIdentifier configuration
            let technologyType = "InferName"
            
            let assayArgs = [AssayRegisterArgs.AssayIdentifier assayIdentifier;TechnologyType technologyType]
            let testAssay = ISADotNet.XLSX.Assays.fromString "" "" "" technologyType "" "" "" assayFileName []
            
            processCommand configuration AssayAPI.register assayArgs
            
            let investigation = ISADotNet.XLSX.Investigation.fromFile (IsaModelConfiguration.getInvestigationFilePath configuration)
            let studyOption = API.Study.tryGetByIdentifier assayIdentifier investigation.Studies
            Expect.isSome studyOption "Study should have been created with the name of the assay but was not"     
            
            let assayOption = API.Assay.tryGetByFileName assayFileName studyOption.Value.Assays
            Expect.isSome assayOption "Assay was not added to newly created study"
            Expect.equal assayOption.Value testAssay "Assay is missing values"
        )
        testCase "StudyNameNotGivenUseAssayNameNoDuplicateStudy" (fun () -> 
            
            let assayIdentifier = "StudyCreatedByAssayRegister"
            let assayFileName = IsaModelConfiguration.getAssayFileName assayIdentifier configuration

            let assayArgs = [AssayRegisterArgs.AssayIdentifier assayIdentifier]
            let testAssay = ISADotNet.XLSX.Assays.fromString "" "" "" "" "" "" "" assayFileName []
            
            processCommand configuration AssayAPI.register assayArgs
            
            let investigation = ISADotNet.XLSX.Investigation.fromFile (IsaModelConfiguration.getInvestigationFilePath configuration)
            
            let studiesWithIdentifiers = investigation.Studies |> List.filter (fun s -> s.Identifier = assayIdentifier)
            
            Expect.equal studiesWithIdentifiers.Length 1 "Duplicate study was added"

            let study = API.Study.tryGetByIdentifier assayIdentifier investigation.Studies |> Option.get

            let assayOption = API.Assay.tryGetByFileName assayFileName study.Assays
            Expect.isSome assayOption "New Assay was not added to study"
            Expect.equal assayOption.Value testAssay "New Assay is missing values"
            Expect.equal study.Assays.Length 2 "Assay missing"
        )
        testCase "StudyNameNotGivenUseAssayNameNoDuplicateAssay" (fun () ->             
            let assayIdentifier = "StudyCreatedByAssayRegister"
            let assayFileName = IsaModelConfiguration.getAssayFileName assayIdentifier configuration

            let assayArgs = [AssayRegisterArgs.AssayIdentifier assayIdentifier]            

            processCommand configuration AssayAPI.register assayArgs
            
            let investigation = ISADotNet.XLSX.Investigation.fromFile (IsaModelConfiguration.getInvestigationFilePath configuration)
            
            let studiesWithIdentifiers = investigation.Studies |> List.filter (fun s -> s.Identifier = assayIdentifier)
            
            Expect.equal studiesWithIdentifiers.Length 1 "Duplicate study was added"

            let study = API.Study.tryGetByIdentifier assayIdentifier investigation.Studies |> Option.get

            let assayOption = API.Assay.tryGetByFileName assayFileName study.Assays
            Expect.isSome assayOption "Assay was removed"
            Expect.equal study.Assays.Length 2 "Duplicate Assay added"
        )
    ]
    |> testSequenced