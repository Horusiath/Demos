namespace ActorsDemo

open System
open System.Threading
open System.Threading.Tasks
open System.Collections.Concurrent
open System.Threading

[<Struct>]
type SystemMessage =
    | Die
    | Restart of exn
    
module Status =
    let [<Literal>] Idle = 0
    let [<Literal>] Occupied = 1
    let [<Literal>] Stopped = 2

[<Sealed>]
type Actor<'state, 'msg>(initState: 'state, handler: 'state -> 'msg -> 'state) as this =
    static let deadLetters = Event<'msg>()
    static let callback: WaitCallback = new WaitCallback(fun o ->
        let actor = o :?> Actor<'state, 'msg>
        actor.Run())
    let mutable status: int = Status.Idle
    let systemMessages = ConcurrentQueue<SystemMessage>()
    let userMessages = ConcurrentQueue<'msg>()
    let mutable state = initState
    
    let stop () =
        Interlocked.Exchange(&status, Status.Stopped) |> ignore
        for msg in userMessages do
            deadLetters.Trigger msg
    member private this.Schedule() =
        if Interlocked.CompareExchange(&status, Status.Occupied, Status.Idle) = Status.Idle
        then ThreadPool.UnsafeQueueUserWorkItem(callback, this) |> ignore
    member private this.Run () =
        let rec loop runs =
            if Volatile.Read(&status) <> Status.Stopped then
                if runs <> 0 then
                    let ok, sysMsg = systemMessages.TryDequeue()
                    if ok then
                        match sysMsg with
                        | Die ->
                            stop()
                            Status.Stopped
                        | Restart error ->
                            //printfn "Restarting actor due to %O" error 
                            state <- initState
                            loop (runs-1)
                    else
                        let ok, msg = userMessages.TryDequeue()
                        if ok then
                            state <- handler state msg
                            loop (runs-1)
                        else Status.Idle
                else Status.Idle
            else Status.Stopped
        try
            let status' = loop 300
            if status' <> Status.Stopped then
              Interlocked.Exchange(&status, Status.Idle) |> ignore
              if systemMessages.Count <> 0 || userMessages.Count <> 0 then
                  this.Schedule()
        with err ->
            Interlocked.Exchange(&status, Status.Idle) |> ignore
            this.Post(Restart err)
    member this.Post(systemMessage: SystemMessage) =
        systemMessages.Enqueue systemMessage
        this.Schedule()
    member __.Post(message: 'msg) = 
        userMessages.Enqueue message
        this.Schedule()
    static member DeadLetters = deadLetters
    interface IDisposable with
        member __.Dispose() = stop()
        