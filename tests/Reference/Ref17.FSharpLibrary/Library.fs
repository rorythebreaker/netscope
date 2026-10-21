namespace Ref.FSharp

open System
open System.Collections.Generic

/// Discriminated unions, records and pattern matching.
type Expr =
    | Num of float
    | Var of string
    | Add of Expr * Expr
    | Mul of Expr * Expr
    | Neg of Expr

type Token = { Kind: string; Text: string; Position: int }

[<Struct>]
type Range = { Start: int; Length: int }

module Eval =
    let rec eval (env: Map<string, float>) expr =
        match expr with
        | Num n -> n
        | Var v ->
            match Map.tryFind v env with
            | Some x -> x
            | None -> failwithf "unbound variable %s" v
        | Add (a, b) -> eval env a + eval env b
        | Mul (a, b) -> eval env a * eval env b
        | Neg e -> -(eval env e)

    let rec simplify expr =
        match expr with
        | Add (Num 0.0, e) | Add (e, Num 0.0) -> simplify e
        | Mul (Num 1.0, e) | Mul (e, Num 1.0) -> simplify e
        | Mul (Num 0.0, _) | Mul (_, Num 0.0) -> Num 0.0
        | Add (a, b) -> Add (simplify a, simplify b)
        | Mul (a, b) -> Mul (simplify a, simplify b)
        | Neg (Neg e) -> simplify e
        | e -> e

    let rec show expr =
        match expr with
        | Num n -> string n
        | Var v -> v
        | Add (a, b) -> sprintf "(%s + %s)" (show a) (show b)
        | Mul (a, b) -> sprintf "%s * %s" (show a) (show b)
        | Neg e -> "-" + show e

module Recursion =
    /// Tail-recursive: compiled to a loop.
    let rec sumTo (acc: int64) (n: int) = if n = 0 then acc else sumTo (acc + int64 n) (n - 1)

    /// Mutually recursive functions: tail calls between methods (the `tail.` prefix).
    let rec isEven n = if n = 0 then true else isOdd (n - 1)
    and isOdd n = if n = 0 then false else isEven (n - 1)

    /// Continuation-passing style: closures capturing the continuation.
    let rec foldBack (f: 'a -> 'b -> 'b) (list: 'a list) (k: 'b -> 'b) (state: 'b) =
        match list with
        | [] -> k state
        | x :: rest -> foldBack f rest (fun acc -> k (f x acc)) state

    let length list = foldBack (fun _ n -> n + 1) list id 0

module Collections =
    let wordFrequencies (text: string) =
        text.Split([| ' '; '\t'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
        |> Seq.map (fun w -> w.ToLowerInvariant())
        |> Seq.countBy id
        |> Seq.sortByDescending snd
        |> List.ofSeq

    let tokenize (input: string) =
        [ let mutable i = 0
          while i < input.Length do
              let c = input.[i]
              if Char.IsDigit c then
                  let start = i
                  while i < input.Length && Char.IsDigit input.[i] do
                      i <- i + 1
                  yield { Kind = "number"; Text = input.Substring(start, i - start); Position = start }
              elif Char.IsWhiteSpace c then
                  i <- i + 1
              else
                  yield { Kind = "symbol"; Text = string c; Position = i }
                  i <- i + 1 ]

    let primes limit =
        seq {
            let sieve = Array.create (limit + 1) true
            for i in 2 .. limit do
                if sieve.[i] then
                    yield i
                    for j in i * i .. i .. limit do
                        sieve.[j] <- false
        }

    let adder n = fun x -> x + n

    let compose = adder 1 >> adder 2 >> (*) 3

    let memoize (f: 'a -> 'b) =
        let cache = Dictionary<'a, 'b>(HashIdentity.Structural)
        fun x ->
            match cache.TryGetValue x with
            | true, v -> v
            | _ ->
                let v = f x
                cache.[x] <- v
                v

type Stack<'T>() =
    let items = List<'T>()
    member _.Push(x: 'T) = items.Add x
    member _.Pop() =
        if items.Count = 0 then invalidOp "empty stack"
        let x = items.[items.Count - 1]
        items.RemoveAt(items.Count - 1)
        x
    member _.Count = items.Count
    interface IEnumerable<'T> with
        member _.GetEnumerator() = (Seq.rev items).GetEnumerator()
        member _.GetEnumerator() : Collections.IEnumerator = ((Seq.rev items) :> Collections.IEnumerable).GetEnumerator()

module Async =
    let delayed (ms: int) value =
        async {
            do! Async.Sleep ms
            return value
        }

    let sumAll (values: int list) =
        task {
            let mutable total = 0
            for v in values do
                let! x = Threading.Tasks.Task.FromResult v
                total <- total + x
            return total
        }
