namespace Yumlog.Native

open System.IO
open System.Text.Json

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
