namespace ActorsDemo

open Akka.Actor
open System
open System.Threading.Tasks
open BenchmarkDotNet.Attributes
open Akkling
open Hopac

[<Struct>]
type CounterMsg =
    | Inc
    | Done of TaskCompletionSource<unit>
   
[<Sealed>]
type Counter() =
    inherit UntypedActor()
    let mutable state = 1
    override __.OnReceive(msg:obj) =
        match msg :?> CounterMsg with
        | Inc -> state <- state+1
        | Done promise ->
            promise.SetResult ()
            state <- 1
    
module HopacActors =
    
    open Hopac
    
    type Counter = Ch<CounterMsg>
    
    let private counter initState (chan: Counter) =
        let mutable state = initState
        job {
            let! msg = chan
            match msg with
            | Inc ->
                state <- state+1
                return ()
            | Done promise ->
                promise.SetResult ()
                state <- initState
                return () }
    
    let create (): Job<Counter> = job {
        let chan = Ch()
        do! Job.foreverServer (counter 1 chan)
        return chan }
    
(*

BenchmarkDotNet=v0.11.5, OS=Windows 10.0.17134.706 (1803/April2018Update/Redstone4)
Intel Core i5-4430 CPU 3.00GHz (Haswell), 1 CPU, 4 logical and 4 physical cores
Frequency=2922917 Hz, Resolution=342.1240 ns, Timer=TSC
.NET Core SDK=3.0.100-preview4-010549
  [Host]     : .NET Core 2.2.3 (CoreCLR 4.6.27414.05, CoreFX 4.6.27414.05), 64bit RyuJIT DEBUG
  DefaultJob : .NET Core 2.2.3 (CoreCLR 4.6.27414.05, CoreFX 4.6.27414.05), 64bit RyuJIT


|           Method |       Mean |      Error |     StdDev | Ratio | RatioSD |       Gen 0 |     Gen 1 |     Gen 2 |  Allocated |
|----------------- |-----------:|-----------:|-----------:|------:|--------:|------------:|----------:|----------:|-----------:|
|      CustomActor |   592.1 ms |  12.358 ms |  36.245 ms |  1.00 |    0.00 |   5000.0000 | 2000.0000 | 1000.0000 | 30703328 B |
| MailboxProcessor |   857.2 ms |   3.313 ms |   3.099 ms |  1.45 |    0.11 | 152000.0000 |         - |         - |      224 B |
|     AkklingActor | 6,880.2 ms | 133.353 ms | 182.535 ms | 11.75 |    0.96 |  23000.0000 | 6000.0000 | 2000.0000 | 64130088 B |
|        AkkaActor |   141.5 ms |   1.736 ms |   1.624 ms |  0.24 |    0.02 |   8000.0000 | 2000.0000 |         - | 32045296 B |
|            Hopac |         NA |         NA |         NA |     ? |       ? |           - |         - |         - |          - |

Hopac:
Process is terminating due to StackOverflowException.

*)

open Hopac

[<MemoryDiagnoser>]
type ActorBenchmark() =
    
    let [<Literal>] Ops = 1_000_000
    
    [<DefaultValue>]
    val mutable private fsActor: MailboxProcessor<CounterMsg>
    
    [<DefaultValue>]
    val mutable private myActor: Actor<int, CounterMsg>
    
    [<DefaultValue>]
    val mutable private actorSystem: ActorSystem
    
    [<DefaultValue>]
    val mutable private akkaActor: IActorRef
    
    [<DefaultValue>]
    val mutable private akklingActor: IActorRef<CounterMsg>
    
    [<DefaultValue>]
    val mutable private hopacActor: HopacActors.Counter
    
    [<GlobalSetup>]
    member this.Setup(): unit =
        // F# native mailbox processor
        this.fsActor <- MailboxProcessor.Start(fun mbox ->
            let rec loop state = async {
                let! msg = mbox.Receive()
                match msg with
                | Inc -> return! loop (state + 1)
                | Done promise ->
                    promise.SetResult ()
                    return! loop 1
            }     
            loop 1)
        
        // My custom actor implementation
        this.myActor <- new Actor<_,_>(1, fun state msg ->
            match msg with
            | Inc -> state + 1
            | Done promise ->
                promise.SetResult ()
                1)
        
        // Akkling & Akka.NET actor
        this.actorSystem <- System.create "system" <| Configuration.defaultConfig()
        let rec counter state msg =
            match msg with
            | Inc -> become (counter (state+1))
            | Done promise ->
                promise.SetResult ()
                become (counter 1)
        this.akklingActor <- spawnAnonymous this.actorSystem <| props (actorOf (counter 1))
        this.akkaActor <- this.actorSystem.ActorOf(Props.Create<Counter>())
        
        // Hopac actor
        this.hopacActor <- HopacActors.create () |> Hopac.run
        
    [<GlobalCleanup>]
    member this.Cleanup(): unit =
        (this.fsActor :> IDisposable).Dispose()
        (this.myActor :> IDisposable).Dispose()
        (this.actorSystem :> IDisposable).Dispose()
        
    [<Benchmark(Baseline=true)>]
    member this.CustomActor() =
        let actor = this.myActor
        for i=1 to Ops do
            actor.Post Inc
        let promise = TaskCompletionSource()
        actor.Post (Done promise)
        promise.Task

    [<Benchmark>]
    member this.MailboxProcessor() =
        let actor = this.fsActor
        for i=1 to Ops do
            actor.Post Inc
        let promise = TaskCompletionSource()
        actor.Post (Done promise)
        promise.Task
        
    [<Benchmark>]
    member this.AkklingActor() =
        let actor = this.akklingActor
        for i=1 to Ops do
            actor <! Inc
        let promise = TaskCompletionSource()
        actor <! (Done promise)
        promise.Task
        
    [<Benchmark>]
    member this.AkkaActor() =
        let actor = this.akkaActor
        for i=1 to Ops do
            actor.Tell Inc
        let promise = TaskCompletionSource()
        actor.Tell (Done promise)
        promise.Task
                
    [<Benchmark>]
    member this.Hopac() =
        let actor = this.hopacActor        
        let promise = TaskCompletionSource()
        job {
            for i=1 to Ops do
                do! Ch.send actor Inc
            do! Ch.send actor (Done promise)
        } |> Hopac.run
        promise.Task