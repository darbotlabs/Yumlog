namespace Yumlog.Native

open System.IO
open System.Text.Json
open System.Text.Json.Nodes

module NativeJson =
    let options =
        let options = JsonSerializerOptions()
        options.WriteIndented <- true
        options.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
        options.PropertyNameCaseInsensitive <- true
        options

    let serialize value =
        JsonSerializer.Serialize(value, options)

    let write (path: string) value =
        match Path.GetDirectoryName(Path.GetFullPath(path)) with
        | null | "" -> ()
        | parent -> Directory.CreateDirectory(parent) |> ignore

        File.WriteAllText(path, serialize value)

    let read<'T> (path: string) =
        let json = File.ReadAllText(path)
        match JsonSerializer.Deserialize<'T>(json, options) with
        | null -> failwith $"Could not deserialize JSON from {path}."
        | value -> value

    /// Both sides of a merge MUST agree on key comparison. SerializeToNode propagates
    /// PropertyNameCaseInsensitive from `options`, while a bare JsonNode.Parse does not -
    /// that asymmetry silently drops file keys whose casing differs from the default key.
    let private nodeOptions = JsonNodeOptions(PropertyNameCaseInsensitive = true)

    /// Deep-merges `overrideNode` onto `baseNode`: objects recurse key-by-key,
    /// everything else replaces wholesale.
    ///
    /// An explicit JSON null is treated as "not specified" and leaves the base value
    /// intact; the config format has no way to express "force this field to null",
    /// deliberately, because null config fields are what this merge exists to prevent.
    let rec private mergeNodes (baseNode: JsonNode | null) (overrideNode: JsonNode | null) : JsonNode | null =
        match baseNode, overrideNode with
        | _, null ->
            match baseNode with
            | null -> null
            | b -> b.DeepClone()
        | null, o -> o.DeepClone()
        | b, o ->
            match b, o with
            | (:? JsonObject as baseObj), (:? JsonObject as overrideObj) ->
                let merged = JsonObject(nodeOptions)

                for entry in baseObj do
                    let mutable existing: JsonNode | null = null

                    let overrideValue =
                        if overrideObj.TryGetPropertyValue(entry.Key, &existing) then existing else null

                    merged[entry.Key] <- mergeNodes entry.Value overrideValue

                for entry in overrideObj do
                    if not (baseObj.ContainsKey entry.Key) then
                        merged[entry.Key] <- mergeNodes null entry.Value

                let result: JsonNode | null = merged
                result
            | _ -> o.DeepClone()

    /// Reads a JSON file layered over `defaults` so that a PARTIAL file only
    /// overrides the keys it actually declares.
    ///
    /// Plain Deserialize is all-or-nothing: every key the file omits comes back as
    /// null / 0 / false regardless of the default value, which silently nulls whole
    /// subsystems and surfaces later as a NullReferenceException far from the cause.
    let readOver<'T> (defaults: 'T) (path: string) : 'T =
        // Parse and merge are wrapped because their exceptions name neither the file nor
        // the offending section. Case-insensitive node options also make two keys that
        // differ only by case a duplicate-key ArgumentException rather than last-wins.
        let merged =
            try
                let defaultsNode = JsonSerializer.SerializeToNode(defaults, options)
                let fileNode = JsonNode.Parse(File.ReadAllText(path), nodeOptions)
                mergeNodes defaultsNode fileNode
            with ex ->
                failwith $"Could not read config from {path}: {ex.Message}"

        match merged with
        | null -> defaults
        | merged ->
            match merged.Deserialize<'T>(options) with
            | null -> failwith $"Could not deserialize JSON from {path}."
            | value -> value
