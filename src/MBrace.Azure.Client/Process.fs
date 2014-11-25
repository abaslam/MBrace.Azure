﻿namespace Nessos.MBrace.Azure.Client

open Nessos.MBrace
open Nessos.MBrace.Azure.Runtime
open Nessos.MBrace.Azure.Runtime.Common
open Nessos.MBrace.Azure.Runtime.Resources
open Nessos.MBrace.Runtime
open Nessos.MBrace.Runtime.Compiler
open Nessos.MBrace.Runtime.Utils.PrettyPrinters
open System
open System.IO
open System.Threading
open System.Reflection

// TODO : Untyped AwaitResult, Vagrant dependencies.
[<AutoSerializable(false); AbstractClass>]
type Process internal (pid : string, ty : Type, pmon : ProcessMonitor) = 
    
    let proc = 
        new Live<_>((fun () -> pmon.GetProcess(pid)), initial = Choice2Of2(exn ("Process not initialized")), 
                    keepLast = true, interval = 500, 
                    stopf = function 
                            | Choice1Of2 p when p.Completed -> true
                            | _ -> false)

    abstract AwaitResultBoxed : unit -> obj
    abstract AwaitResultBoxedAsync : unit -> Async<obj>

    member internal __.ProcessEntity = proc
    member internal __.DistributedCancellationTokenSource = 
        DistributedCancellationTokenSource.FromUri(new Uri(proc.Value.CancellationUri))
        
    member __.Id = pid
    member __.Name = proc.Value.Name
    member __.Type = ty

    member __.InitializationTime = proc.Value.InitializationTime.ToLocalTime()
    
    member __.ExecutionTime = 
        let s = 
            if proc.Value.Completed then proc.Value.CompletionTime
            else DateTime.UtcNow
        s - proc.Value.InitializationTime
    
    member __.Completed = proc.Value.Completed
    member __.Kill() = __.DistributedCancellationTokenSource.Cancel()

[<AutoSerializable(false)>]
type Process<'T> internal (pid : string, pmon : ProcessMonitor) = 
    inherit Process(pid, typeof<'T>, pmon) 

    override __.AwaitResultBoxed () : obj =__.AwaitResultBoxedAsync() |> Async.RunSynchronously 
    override __.AwaitResultBoxedAsync () : Async<obj> =
        async {
            let rc : ResultCell<Result<'T>> = ResultCell.FromUri<_>(new Uri(__.ProcessEntity.Value.ResultUri))
            let! r = rc.AwaitResult()
            return r.Value :> obj
        }


    member __.AwaitResult() : 'T = __.AwaitResultAsync() |> Async.RunSynchronously
    member __.AwaitResultAsync() : Async<'T> = 
        async {
            let rc : ResultCell<Result<'T>> = ResultCell.FromUri<_>(new Uri(__.ProcessEntity.Value.ResultUri))
            let! r = rc.AwaitResult()
            return r.Value
        }

    static member internal Create(pid : string, ty : Type, pmon : ProcessMonitor) : Process =
        let processT = typeof<Process<_>>.GetGenericTypeDefinition().MakeGenericType [| ty |]
        let flags = BindingFlags.NonPublic ||| BindingFlags.Instance
        let culture = System.Globalization.CultureInfo.InvariantCulture
        Activator.CreateInstance(processT, flags, null, [|pid :> obj ; pmon :> obj |], culture) :?> Process
        //Activator.CreateInstance(processT, [|pid :> obj ; pmon :> obj |]) :?> Process
