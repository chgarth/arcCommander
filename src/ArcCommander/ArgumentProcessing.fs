﻿namespace ArcCommander

open System.Diagnostics
open Microsoft.FSharp.Reflection
open System
open System.IO
open System.Diagnostics
open System.Text
open System.Text.Json
open Argu


/// Functions for processing arguments.
module ArgumentProcessing = 

    /// Carries the argument value to the ArcCommander API functions, use 'containsFlag' and 'getFieldValuByName' to access the value.
    type Argument =
        | Field of string
        | Flag

    /// Argument with additional information.
    type AnnotatedArgument = 
        {
            Arg         : Argument Option
            Tooltip     : string
            IsMandatory : bool
            IsFlag      : bool
        }
    
    let createAnnotatedArgument arg tt mand isFlag = 
        {
            Arg = arg
            Tooltip = tt 
            IsMandatory = mand
            IsFlag = isFlag
        }

    /// Returns true if the argument flag of name k was given by the user.
    let containsFlag k (arguments : Map<string,Argument>) =
        let log = Logging.createLogger "ArgumentProcessingContainsFlagLog"
        match Map.tryFind k arguments with
        | Some (Field _ )   -> 
            log.Fatal($"Argument {k} is not a flag, but a field.")
            raise (Exception(""))
        | Some (Flag)       -> true
        | None              -> false

    /// Returns the value given by the user for name k.
    let tryGetFieldValueByName k (arguments : Map<string,Argument>) = 
        match Map.tryFind k arguments with
        | Some (Field "") -> None
        | Some (Field v) -> Some v
        | Some Flag -> None
        | None -> None

    /// Returns the value given by the user for name k.
    let getFieldValueByName k (arguments : Map<string,Argument>) = 
        let log = Logging.createLogger "ArgumentProcessingGetFieldValueByNameLog"
        match Map.find k arguments with
        | Field v -> v
        | Flag -> 
            log.Fatal($"Argument {k} is not a field, but a flag.")
            raise (Exception(""))

    /// For a given discriminated union value, returns the field name and the value.
    let private splitUnion (x : 'a) = 
        match FSharpValue.GetUnionFields(x, typeof<'a>) with
        | case, v -> case.Name,v

    /// For a given discriminated union value, returns the field name and the value string.
    let private unionToString (x : 'a) = 
        match splitUnion x with
        | (field,value) -> field, string value.[0]

    /// Returns given attribute from property info as optional.
    let private containsCustomAttribute<'a> (case : UnionCaseInfo) =   
        let attributeType = typeof<'a>
        match case.GetCustomAttributes (attributeType) with
        | [||] -> false
        | _ -> true
   
    /// Returns true if a value in the array contains the Mandatory attribute but is empty.
    let containsMissingMandatoryAttribute (arguments : (string * AnnotatedArgument) []) =
        arguments
        |> Seq.exists (fun (k,v) ->
            v.Arg.IsNone && v.IsMandatory
        )

    /// Adds all union cases of 'T which are missing to the list.
    let groupArguments (args : 'T list when 'T :> IArgParserTemplate) =
        let log = Logging.createLogger "ArgumentProcessingGroupArgumentsLog"
        let m = 
            args 
            |> List.map splitUnion
            |> Map.ofList
        FSharpType.GetUnionCases(typeof<'T>)
        |> Array.map (fun unionCase ->
            let isMandatory = containsCustomAttribute<MandatoryAttribute>(unionCase) 
            let fields = unionCase.GetFields()
            match fields with 
            | [||] -> 
                let toolTip = (FSharpValue.MakeUnion (unionCase, [||]) :?> 'T).Usage
                let value,isFlag = if Map.containsKey unionCase.Name m then Some Flag,true else None,true             
                unionCase.Name,createAnnotatedArgument value toolTip isMandatory isFlag
            | [|c|] when c.PropertyType.Name = "String" -> 
                let toolTip = (FSharpValue.MakeUnion (unionCase, [|box ""|]) :?> 'T).Usage
                let value, isFlag = 
                    match Map.tryFind unionCase.Name m with
                    | Some value -> 
                        Field (string value.[0])
                        |> Some,
                        false
                    | None -> None, false
                unionCase.Name, createAnnotatedArgument value toolTip isMandatory isFlag
            | _ ->
                log.Fatal($"Cannot parse argument {unionCase.Name} because its parsing rules were not yet implemented.")
                raise (Exception(""))
        )

    ///// Creates an isa item used in the investigation file
    //let isaItemOfArguments (item:#InvestigationFile.ISAItem) (parameters : Map<string,Argument>) = 
    //    parameters
    //    |> Map.iter (fun k v -> 
    //        match v with
    //        | Field s -> 
    //            InvestigationFile.setKeyValue (System.Collections.Generic.KeyValuePair(k,s)) item
    //            |> ignore
    //        | Flag ->
    //            ()
    //    )
    //    item

    /// Functions for asking the user to input values via an editor prompt.
    module Prompt = 

        /// Creates a MD5 hash from an input string.
        let private MD5Hash (input : string) =
            use md5 = System.Security.Cryptography.MD5.Create()
            input
            |> Encoding.ASCII.GetBytes
            |> md5.ComputeHash
            |> Seq.map (fun c -> c.ToString("X2"))
            |> Seq.reduce (+)
    
        /// Starts a program at the given path with the given arguments.
        let private runProcess rootPath arg =
            let p = 
                new ProcessStartInfo
                    (rootPath,arg) 
                |> Process.Start
            p.WaitForExit()

        /// Deletes the file.
        let private delete (path:string) = 
            File.Delete(path) 

        /// Writes a text string to a path.
        let private write (path : string) (text : string) = 
            let textWithEnvLineBreaks = text.Replace("\n", System.Environment.NewLine)
            use w = new StreamWriter(path, false, Encoding.UTF8)
            w.Write(textWithEnvLineBreaks)
            w.Flush()
            w.Close()
        
        /// Writes a text string to a path, Creates the directory if it doesn't exist yet.
        let private writeForce (path : string) (text : string) = 
            delete path
            FileInfo(path).Directory.Create()
            write path text

        /// Reads the content of a file.
        let private read (path : string) = 
            use r = new StreamReader(path)
            r.ReadToEnd()

        /// Serializes annotated argument in yaml format (key:value).
        ///
        /// For each value, a comment is created and put above the line using the given commentF function.
        let private serializeAnnotatedArguments (arguments : (string * AnnotatedArgument) []) =
            let header = 
                """# Not all mandatory input arguments were given
# Please fill out at least all mandatory fields by providing a value to the key in the form of "key:value"
# Commented out flags (keys with no ":") can be set by removing the # in front of them
# When finished save and close the editor"""
            arguments
            |> Array.map (fun (key,arg) -> 
                let comment = 
                    if arg.IsMandatory then
                        sprintf "Mandatory: %s" arg.Tooltip
                    elif arg.IsFlag then
                        sprintf "Remove # below to set flag: %s" arg.Tooltip
                    else sprintf "%s" arg.Tooltip
                let value = 
                    match arg.Arg with
                    | Some (Flag)           -> sprintf "%s" key
                    | Some (Field v)        -> sprintf "%s:%s" key v
                    | None when arg.IsFlag  -> sprintf "#%s" key
                    | None                  -> sprintf "%s:" key
                sprintf "#%s\n%s" comment value
            )
            |> Array.reduce (fun a b -> a + "\n\n" + b)
            |> sprintf "%s\n\n%s" header
    
        /// Splits the string at the first occurence of the char.
        let private splitAtFirst (c : char) (s : string) =
            match s.Split c with
            | [|k|]     -> k.Trim(), Flag 
            | [|k ; v|] -> k.Trim(), v.Trim() |> Field 
            | a         -> a.[0].Trim(), Array.skip 1 a |> Array.reduce (fun a b ->sprintf "%s%c%s" a c b) |> fun v -> v.Trim() |> Field

        /// Deserializes yaml format (key:value) arguments.
        let private deserializeArguments (s:string) =
            s.Split '\n'
            |> Array.choose (fun x -> 
                if x.StartsWith "#" then
                    None
                else 
                    splitAtFirst ':' x |> Some
            )

        /// Opens a textprompt containing the result of the serializeF to the user. Returns the deserialized user input.
        let createQuery editorPath serializeF deserializeF inp =
            let log = Logging.createLogger "ArgumentProcessingPromptCreateQueryLog"
            
            let yamlString = serializeF inp
            let hash = MD5Hash yamlString
            let filename = sprintf "%s.yml" hash
            let filePath = Path.Combine(Path.GetTempPath(), filename)

    
            writeForce filePath yamlString
            try
                runProcess editorPath filePath
                let p = read filePath |> deserializeF
                delete filePath
                p

            with
            | err -> 
                delete filePath
                log.Fatal($"Could not parse query:\n {err.ToString()}")
                raise (Exception(""))

        /// Opens a textprompt containing the result of the serialized input parameters. Returns the deserialized user input.
        let createArgumentQuery editorPath (arguments : (string * AnnotatedArgument) []) = 
            arguments
            |> createQuery editorPath serializeAnnotatedArguments deserializeArguments 
            |> Map.ofArray

        /// If parameters are missing a mandatory field, opens a textprompt containing the result of the serialized input parameters. Returns the deserialized user input.
        let createMissingArgumentQuery editorPath (arguments : (string * AnnotatedArgument) []) = 
            let mandatoryArgs = arguments |> Array.choose (fun (key,arg) -> if arg.IsMandatory then Some key else None)
            let queryResults = createArgumentQuery editorPath arguments
            let stillMissingMandatoryArgs =
                mandatoryArgs
                |> Array.map (fun k -> 
                    let field = tryGetFieldValueByName k queryResults
                    field = None || field = Some ""
                )
                |> Array.reduce ((||))
            stillMissingMandatoryArgs,queryResults
            
        /// Removes additional annotation (isMandatory and tooltip) from argument.
        let deannotateArguments (arguments : (string * AnnotatedArgument) []) =
            arguments
            |> Array.choose (fun (k,v) -> 
                match v.Arg with 
                | Some arg -> Some (k,arg)
                | None when v.IsFlag -> None
                | None -> Some (k,Field ""))
            |> Map.ofArray

        /// Serializes the output of a writer and converts it into a string.
        let serializeXSLXWriterOutput (writeF : 'A -> seq<ISADotNet.XLSX.SparseRow>) (inp : 'A) = 
            writeF inp
            |> Seq.map (fun r -> 
                sprintf "%s:%s"
                    (ISADotNet.XLSX.SparseRow.tryGetValueAt 0 r |> Option.get |> fun s -> s.TrimStart())
                    (ISADotNet.XLSX.SparseRow.tryGetValueAt 1 r |> Option.get)
            )
            |> Seq.reduce (fun a b -> a + "\n" + b)

        /// Opens a textprompt containing the serialized input item. Returns item updated with the deserialized user input.
        let createIsaItemQuery editorPath
            (writeF : 'A -> seq<ISADotNet.XLSX.SparseRow>)
            (readF : System.Collections.Generic.IEnumerator<ISADotNet.XLSX.SparseRow> -> 'A)
            (isaItem : 'A) = 

            let log = Logging.createLogger "ArgumentProessingPromptCreateIsaItemQueryLog"

            let header = 
                """# For editing the selected item, just provide the desired values for the keys below or change preexisting values
# "key:value"
# When finished save and close the editor"""

            let serializeF = serializeXSLXWriterOutput writeF >> sprintf "%s\n\n%s" header

            let deserializeF (s : string) : 'A =
                s.Split('\n')
                |> Seq.choose (fun x ->
                    x.Replace("\013", "")
                    |> fun x ->
                        if x.Length = 0 || x.[0] = '#' then None
                        else
                            Some (
                                match splitAtFirst ':' x with
                                | k, Field v ->
                                    ISADotNet.XLSX.SparseRow.fromValues [k;v]
                                | _ -> log.Fatal("File was corrupted in Editor."); raise (Exception(""))
                            )
                )
                |> fun rs -> readF (rs.GetEnumerator()) 
            createQuery editorPath serializeF deserializeF isaItem

        /// Opens a textprompt containing the serialized iniData. Returns the iniData updated with the deserialized user input.
        let createIniDataQuery editorPath (iniData : IniParser.Model.IniData) =
            let log = Logging.createLogger "ArgumentProessingPromptCreateIniDataQueryLog"
            let serializeF inp = 
                IniData.flatten inp
                |> Seq.map (fun (n,v) -> n + "=" + v)
                |> Seq.reduce (fun a b -> a + "\n" + b)
            let deserializeF (s:string) =
                s.Split '\n'
                |> Array.map (fun x ->
                    match splitAtFirst '=' x with
                    | k, Field v -> k,v
                    | _ -> log.Fatal("File was corrupted in Editor."); raise (Exception(""))
                )
                |> IniData.fromNameValuePairs
            createQuery editorPath serializeF deserializeF iniData

    /// Serializes a JSON item into a string.
    let serializeToString (item : 'A) =
        JsonSerializer.Serialize(item, ISADotNet.JsonExtensions.options)

    /// Serializes a JSON item into a file.
    let serializeToFile (p : string) (item : 'A) =
        JsonSerializer.Serialize(item, ISADotNet.JsonExtensions.options)
        |> fun s -> File.WriteAllText(p, s)
