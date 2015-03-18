﻿module internal Nessos.Vagabond.SliceCompiler

open System
open System.IO
open System.Collections.Generic
open System.Text.RegularExpressions
open System.Reflection

open Mono.Cecil

open Nessos.FsPickler

open Nessos.Vagabond.Utils
open Nessos.Vagabond.SliceCompilerTypes
open Nessos.Vagabond.AssemblyParser
open Nessos.Vagabond.DependencyAnalysis

/// creates a path for provided assembly name so that
/// invalid characters are stripped and overwrites are avoided.
let getAssemblyPath =
    let invalidChars = new String(Path.GetInvalidFileNameChars()) |> Regex.Escape
    let regex = new Regex(sprintf "[%s]" invalidChars, RegexOptions.Compiled)
    fun (path : string) (name : string) ->
        let stripped = regex.Replace(name, "")
        let rec getSuffix i =
            let path = 
                if i = 0 then Path.Combine(path, stripped + ".dll")
                else Path.Combine(path, sprintf "%s-%d.dll" name i)

            if File.Exists path then getSuffix (i+1)
            else path

        getSuffix 0 

/// create an initial, empty compiler state

let initCompilerState (profiles : IDynamicAssemblyProfile list) (outDirectory : string) =
    let uuid = Guid.NewGuid()
    let mkSliceName (name : string) (id : int) = sprintf "%s_%O_%d" name uuid id
    let assemblyRegex = Regex(sprintf "^(.*)_%O_([0-9]+)" uuid, RegexOptions.Compiled)
    let tryExtractDynamicAssemblyId (assemblyName : string) =
        let m = assemblyRegex.Match(assemblyName)
        if m.Success then 
            let dynamicName = m.Groups.[1].Value
            let sliceId = int <| m.Groups.[2].Value
            Some (dynamicName, sliceId)
        else
            None
    {
        CompilerId = Guid.NewGuid()
        Profiles = profiles
        OutputDirectory = outDirectory

        DynamicAssemblies = Map.empty

        TryGetDynamicAssemblyId = tryExtractDynamicAssemblyId
        CreateAssemblySliceName = mkSliceName
    }


/// compiles a slice of given dynamic assembly snapshot

let compileDynamicAssemblySlice (state : DynamicAssemblyCompilerState)
                                (assemblyState : DynamicAssemblyState)
                                (typeData : Map<string, TypeParseInfo>)
                                (slice : AssemblyDefinition) =

    // prepare slice info
    let sliceId = assemblyState.GeneratedSlices.Count + 1
    let name = state.CreateAssemblySliceName assemblyState.Name.Name sliceId
    let target = getAssemblyPath state.OutputDirectory name

    // update assembly name & write to disk
    do slice.Name.Name <- name
    do slice.Write(target)

    // load new slice to System.Reflection
    let assembly = Assembly.ReflectionOnlyLoadFrom(target)
        
    // collect pickleable static fields
    let pickleableFields = 
        typeData 
        |> Seq.map (function KeyValue(_,InCurrentSlice(_,fields)) -> fields | _ -> [||] ) 
        |> Array.concat

    let sliceInfo = 
        { 
            SourceId = state.CompilerId
            Assembly = assembly 
            DynamicAssemblyQualifiedName = assemblyState.DynamicAssembly.FullName 
            SliceId = sliceId 
            StaticFields = pickleableFields
        }

    // update generated slices
    let generatedSlices = assemblyState.GeneratedSlices.Add(sliceId, sliceInfo)
        
    // update the type index
    let mapTypeIndex (id : string) (info : TypeParseInfo) =
        match info with
        | AlwaysIncluded -> InAllSlices
        | InCurrentSlice _ -> InSpecificSlice sliceInfo
        | InPastSlice (slice = slice) -> InSpecificSlice slice
        | Erased -> InNoSlice

    let typeIndex = typeData |> Map.map mapTypeIndex

    // return state
    let assemblyState = { assemblyState with GeneratedSlices = generatedSlices ; TypeIndex = typeIndex }
    let dynamicAssemblyIndex = state.DynamicAssemblies.Add(assemblyState.DynamicAssembly.FullName, assemblyState)
    let state = { state with DynamicAssemblies = dynamicAssemblyIndex}

    sliceInfo, state

/// compiles a collection of assemblies

let compileDynamicAssemblySlices ignoreF requireLoaded (state : DynamicAssemblyCompilerState) (assemblies : Assembly list) =
    try
        // resolve dynamic assembly dependency graph
        let parsedDynamicAssemblies = parseDynamicAssemblies ignoreF requireLoaded state assemblies

        // exceptions are handled explicitly so that returned state reflects the last successful compilation
        let compileSlice (state : DynamicAssemblyCompilerState, accumulator : Exn<DynamicAssemblySlice list>)
                            (typeData, dynAsmb, _, assemblyDef) =

            match accumulator with
            | Success slices ->
                try
                    let slice, state = compileDynamicAssemblySlice state dynAsmb typeData assemblyDef
                    state, Success (slice :: slices)
                with e ->
                    state, Error e
            | Error _ -> state, accumulator

        List.fold compileSlice (state, Success []) parsedDynamicAssemblies

    with e -> state, Error e