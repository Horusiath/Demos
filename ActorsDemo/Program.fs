open ActorsDemo
open BenchmarkDotNet.Running
open System.Reflection

[<EntryPoint>]
let main argv =
    BenchmarkSwitcher.FromAssembly(Assembly.GetExecutingAssembly()).Run(argv) |> ignore
    0
