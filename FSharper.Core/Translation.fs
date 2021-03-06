﻿namespace FSharper.Core

open Microsoft.CodeAnalysis.CSharp
open Microsoft.FSharp.Compiler.Ast
open Microsoft.FSharp.Compiler.Range
open Microsoft.FSharp.Compiler.SourceCodeServices.Structure
open Microsoft.FSharp.Compiler.SourceCodeServices 
open Fantomas

module Range = 
    let addLine file (range:range) = 

        let pos1 = mkPos 0 0
        let pos2 = mkPos (range.EndLine + 1) range.EndColumn

        mkRange file pos1 pos2

    let moveDownLine file (range:range) = 
        let pos1 = mkPos (range.StartLine + 1) range.StartColumn
        let pos2 = mkPos (range.EndLine + 1) range.EndColumn

        mkRange file pos1 pos2

module TreeOps = 
    let checker = FSharpChecker.Create()

    let getUntypedTree (file, input) = 
        // Get compiler options for the 'project' implied by a single script file
        let projectOptions, _errors =  
            checker.GetProjectOptionsFromScript(file, input)
            |> Async.RunSynchronously

        let parsingOptions, _errors = checker.GetParsingOptionsFromProjectOptions(projectOptions)

        // Run the first phase (untyped parsing) of the compiler
        let parseFileResults = 
            checker.ParseFile(file, input, parsingOptions) 
            |> Async.RunSynchronously

        match parseFileResults.ParseTree with
        | Some tree -> tree
        | None -> failwith "Something went wrong during parsing!"


    let rec toSynPat p = 
        match p with 
        | Pat.LongIdent (a, b, c, d, e) -> SynPat.LongIdent (a, b, c, d, e, range0)
        | Pat.Wild -> SynPat.Wild range0
        | Pat.Named (a,b,c,d)-> SynPat.Named (toSynPat a,b,c,d, range0)

    let rec toBinding b = 
        let (LetBind (accessibility, kind, mustInline, isMutable, attrs, valData, headPat, expr)) = b
        SynBinding.Binding (accessibility, kind, mustInline, isMutable, attrs, PreXmlDocEmpty, valData, toSynPat headPat, None, toSynExpr expr,range0, SequencePointAtBinding range0)

    and toSynMatchExpr m = 
        match m with 
        | MatchClause.Clause (a,b,c) -> SynMatchClause.Clause (a, b |> Option.map toSynExpr,toSynExpr c,range0, SequencePointInfoForTarget.SequencePointAtTarget)
        
    and 
        toSynExpr (expr:Expr): SynExpr = 
        match expr with  
        | Expr.Paren x -> SynExpr.Paren (toSynExpr x, range0, None, range0)
        | Expr.Const x -> SynExpr.Const (x, range0)
        | Expr.Typed (expr, typeName) -> SynExpr.Typed (toSynExpr expr, typeName, range0)
        | Expr.Tuple xs -> SynExpr.Tuple (xs |> List.map toSynExpr, [range0], range0) 

        | Expr.New (isProtected, typeName, expr) -> SynExpr.New (isProtected, typeName, toSynExpr expr, range0)
        //| While of whileSeqPoint:SequencePointInfoForWhileLoop * whileExpr:Expr * doExpr:Expr
        //| For of forSeqPoint:SequencePointInfoForForLoop * ident:Ident * identBody:Expr * bool * toBody:Expr * doBody:Expr

        | Expr.ForEach (a,b,c,d,e,f) -> SynExpr.ForEach (a,b,c,toSynPat d,toSynExpr e, toSynExpr f,range0)

        | Expr.ArrayOrListOfSeqExpr (isArray, expr) -> SynExpr.ArrayOrListOfSeqExpr (isArray, toSynExpr expr, range0)
        | Expr.CompExpr (isArrayOrList, isNotNakedRefCell, expr) -> SynExpr.CompExpr (isArrayOrList, isNotNakedRefCell, toSynExpr expr, range0)

        | Expr.Lambda (a,b,c,d) -> SynExpr.Lambda (a,b,c, toSynExpr d, range0)  //of  fromMethod:bool * inLambdaSeq:bool * args:SynSimplePats * body:Expr

        //| Assert of expr:Expr
        | Expr.Match (a,b,c,d) -> SynExpr.Match (a,toSynExpr b,c |> List.map toSynMatchExpr, d, range0)

        | Expr.App (a,b,c,d) -> SynExpr.App (a,b, toSynExpr c, toSynExpr d, range0)
        | Expr.TypeApp (a,b) -> SynExpr.TypeApp (toSynExpr a, range0, b, [], None, range0, range0)

        | Expr.LetOrUse (a,b,c,d) -> // of isRecursive:bool * isUse:bool * bindings:SynBinding list * body:Expr
            
            SynExpr.LetOrUse (a,b,c |> List.map toBinding, toSynExpr d, range0)

        //| TryWith of tryExpr:Expr * withCases:SynMatchClause list * trySeqPoint:SequencePointInfoForTry * withSeqPoint:SequencePointInfoForWith

        //| TryFinally of tryExpr:Expr * finallyExpr:Expr * trySeqPoint:SequencePointInfoForTry * finallySeqPoint:SequencePointInfoForFinally

        //| Lazy of Expr

        | Expr.Sequential (a,b,c,d) -> SynExpr.Sequential (a,b,toSynExpr c,toSynExpr d, range0)

        | Expr.IfThenElse (a,b,c,d,e) -> SynExpr.IfThenElse (toSynExpr a, toSynExpr b,c |> Option.map toSynExpr ,d,e, range0, range0) 

        | Expr.Ident s -> SynExpr.Ident (Ident(s, range0))
        | Expr.LongIdent (a,b) -> SynExpr.LongIdent (a,b, None, range0)

        | Expr.LongIdentSet (id, e) -> SynExpr.LongIdentSet (id, toSynExpr e, range0) //of longDotId:LongIdentWithDots * expr:Expr
        | Expr.DotGet (e,a) -> SynExpr.DotGet (toSynExpr e, range0, a, range0)
        //| DotSet of Expr * longDotId:LongIdentWithDots * Expr
        | Expr.Set (left, right) -> SynExpr.Set (toSynExpr left, toSynExpr right, range0)
        //| DotIndexedGet of Expr * SynIndexerArg list

        //| DotIndexedSet of objectExpr:Expr * indexExprs:SynIndexerArg list * valueExpr:Expr
        //| NamedIndexedPropertySet of longDotId:LongIdentWithDots * Expr * Expr
        //| TypeTest of  expr:Expr * typeName:SynType
        //| Upcast of  expr:Expr * typeName:SynType 
        | Expr.Downcast (a,b) -> SynExpr.Downcast (toSynExpr a,b,range0)
        //| InferredUpcast of  expr:Expr 

        //| InferredDowncast of  expr:Expr 
        | Expr.Null -> SynExpr.Null range0
        //| AddressOf of  isByref:bool * Expr 
        //| TraitCall of SynTypar list * SynMemberSig * Expr 
        //| LetOrUseBang    of bindSeqPoint:SequencePointInfoForBinding * isUse:bool * isFromSource:bool * SynPat * Expr * Expr

        //| DoBang      of expr:Expr
        //| Fixed of expr:Expr

        | Expr.ReturnFromIf e -> toSynExpr e //SynExpr.Const (SynConst.Unit, range0)


    type MatchOrAnd = 
        | Value of Expr
        | Or of Expr list
    
    let rewriteReturnInIf tree = 

        let rec isReturnFrom = function 
            | Expr.ReturnFromIf _ -> true
            | Expr.Sequential (_,_,c,d) -> isReturnFrom c || isReturnFrom d
            | Expr.IfThenElse (a,b,c,d,e) -> isReturnFrom b || c |> Option.map isReturnFrom |> Option.defaultValue false
            | Expr.App (a,b,c,d) -> isReturnFrom c || isReturnFrom d
            | _ -> false

        let rec removeReturn = function 
            | Expr.ReturnFromIf e -> e
            | Expr.Sequential (a,b,c,d) -> Expr.Sequential (a,b, removeReturn c, removeReturn d)
            | Expr.IfThenElse (a,b,c,d,e) -> Expr.IfThenElse (a, removeReturn b, c |> Option.map removeReturn, d, e)
            | Expr.App (a,b,c,d) -> Expr.App (a,b, removeReturn c, removeReturn d)
            | e -> e

        let rec walker tree = 
            match tree with 
            | Expr.Sequential (s1,s2,s3,s4) -> 
                match s3 with 
                | Expr.IfThenElse (x,y,z,i,j) when Option.isNone z && isReturnFrom y -> 
                    Expr.IfThenElse (x, removeReturn y, Some s4, i, j)

                | Expr.IfThenElse (x,y,z,i,j) when Option.map isReturnFrom z = Some true -> 
                    let first = Expr.Sequential (SequencePointInfoForSeq.SequencePointsAtSeq, true, y, s4)  
                    Expr.IfThenElse (x,first, z |> Option.map removeReturn, i, j)                

                | _ ->  Expr.Sequential (s1,s2,walker s3,walker s4)

            | Expr.IfThenElse (a,b,c,d,e) -> 
                let a = walker a
                let b = walker b
                let c = c |> Option.map walker
                Expr.IfThenElse (a,b,c,d,e)

            | Expr.App (a,b,c,d) -> 

                let c = walker c
                let d = walker d
                Expr.App (a,b,c,d)

            | Expr.ForEach (a,b,c,d,e,f) -> 
                Expr.ForEach (a,b,c,d, walker e, walker f)

            | Expr.Match (a,b,c,d) -> 
                let c = c |> List.map (fun (MatchClause.Clause (a,b,c)) -> MatchClause.Clause (a,b, removeReturn c))
                Expr.Match (a,walker b,c,d)
            | _ -> tree
        walker tree

    let rec containsExpr f tree = 
        let containsExpr = containsExpr f
        match tree with 
        | Expr.Sequential (s1,s2,s3,s4) -> 
            containsExpr s3 || containsExpr s4

        | Expr.IfThenElse (a,b,c,d,e) -> containsExpr a || containsExpr b

        | Expr.App (a,b,c,d) -> 
            containsExpr c || containsExpr d

        | Expr.ForEach (a,b,c,d,e,f) -> 
            containsExpr e || containsExpr f

        | Expr.Match (a,b,c,d) -> 
            c |> List.map (fun (MatchClause.Clause (a,b,c)) -> containsExpr c) |> List.reduce (||)

        | x when f x -> true
        | _ -> false
    

    let rec rewriteMatchIs tree = 
        //let whenAnd x y = 
            //match x,y with 
            //| Some (MatchOrAnd.Value x), Some (MatchOrAnd.Value y) -> 
            //    let e = Expr.App(ExprAtomicFlag.NonAtomic, true, Expr.Ident "op_BooleanAnd", y)
            //    (Expr.App(ExprAtomicFlag.NonAtomic, false, e, x)) |> MatchOrAnd.Value |> Some
            //| Some (MatchOrAnd.Value x), Some (MatchOrAnd.Or y) ->  x::y |> MatchOrAnd.Or |> Some
            //| Some (MatchOrAnd.Or y), Some (MatchOrAnd.Value x) ->  x::y |> MatchOrAnd.Or |> Some
            //| Some (MatchOrAnd.Or x), Some (MatchOrAnd.Or y) ->  x @ y |> MatchOrAnd.Or |> Some
            //| Some x, None -> Some x
            //| None, Some x -> Some x
            //| None, None -> None

        let whenAnd x y = 
            match x,y with 
            | Some x, Some y -> 
                let e = Expr.App(ExprAtomicFlag.NonAtomic, true, Expr.Ident "op_BooleanAnd", x)
                (Expr.App(ExprAtomicFlag.NonAtomic, false, e, y)) |> Some
            //| Some (MatchOrAnd.Value x), Some (MatchOrAnd.Or y) ->  x::y |> MatchOrAnd.Or |> Some
            //| Some (MatchOrAnd.Or y), Some (MatchOrAnd.Value x) ->  x::y |> MatchOrAnd.Or |> Some
            //| Some (MatchOrAnd.Or x), Some (MatchOrAnd.Or y) ->  x @ y |> MatchOrAnd.Or |> Some
            | Some x, None -> Some x
            | None, Some x -> Some x
            | None, None -> None
            

        let rec removeMatchPlaceholder testExpr whenExpr firstResult secondResult (walker, a,b,c,d,e) = 

            match testExpr with 
            | Expr.Match (f,g,h,i) -> 
                let h = 
                    match h with 
                    | MatchClause.Clause(j,None,_)::[] -> 
                        match secondResult with 
                        | Some secondResult -> 
                            [ 
                                MatchClause.Clause(j,None,firstResult); 
                                MatchClause.Clause(SynPat.Wild range0,None, secondResult); 
                            ]
                        | None -> 
                            [
                                MatchClause.Clause(j,None,firstResult); 
                                MatchClause.Clause(SynPat.Wild range0,None, Expr.Const SynConst.Unit); ]

                Expr.Match (f,g,h,i)
            | Expr.App (f,g,h,i) -> 
                match h,i with 
                | Expr.Ident x, Expr.Match _ when x = "not"  -> 
                    removeMatchPlaceholder i whenExpr
                        (match secondResult with | Some x -> x | None -> Expr.Const SynConst.Unit) (Some firstResult)
                        (walker, a,b,c,d,e)
                | Expr.Ident x, _ when x = "op_BooleanOr" -> 
                    match removeMatchPlaceholder i [] firstResult secondResult (walker, a,b,c,d,e) with 
                    | Expr.Match (f,g,h,i) -> 

                        let h = 
                            match h |> List.rev with 
                            | MatchClause.Clause(_, None, secondResult)::MatchClause.Clause(j,None, firstResult)::[] -> 

                                let whenOr = whenExpr |> List.choose id |> List.map (fun orExp -> MatchClause.Clause(j,Some orExp, secondResult) )
                                let baseChecks = 
                                    [   
                                        MatchClause.Clause(j,None,firstResult); 
                                        MatchClause.Clause(SynPat.Wild range0,None, secondResult); 
                                    ]

                                whenOr @ baseChecks
                            
                                //let xs = 
                                //    xs |> List.map (fun (MatchClause.Clause(j,_,firstResult)) -> 
                                //        whenExpr |> List.choose id |> List.map (fun orExp -> MatchClause.Clause(j,Some orExp, firstResult) ) )
                                //    |> List.concat

                                //x :: xs |> List.rev
                            | MatchClause.Clause(_, None, secondResult)::MatchClause.Clause(j,None, firstResult)::xs -> 

                                let ys = 
                                        xs |> List.map (fun (_) -> 
                                            whenExpr |> List.choose id |> List.map (fun orExp -> MatchClause.Clause(j,Some orExp, secondResult) ) )
                                        |> List.concat

                                let baseChecks = 
                                    [   
                                        MatchClause.Clause(j,None,firstResult); 
                                        MatchClause.Clause(SynPat.Wild range0,None, secondResult); 
                                    ]
                                xs @ ys @ baseChecks
                            | [] -> []

                        Expr.Match (f,g,h,i)
                    | e -> e
                | Expr.Ident x, _ when x = "op_BooleanAnd" -> 
                    match removeMatchPlaceholder i [] firstResult secondResult (walker, a,b,c,d,e) with 
                    | Expr.Match (f,g,h,i) -> 

                        let h = 
                            match h with 
                            | MatchClause.Clause(j,k,firstResult)::MatchClause.Clause(m,n,secondResult)::[] -> 
                            
                                match whenExpr |> List.map (whenAnd k) with 
                                | x::[] -> 
                                    [   MatchClause.Clause(j, x, firstResult); 
                                        MatchClause.Clause(SynPat.Wild range0,None, secondResult); 
                                    ]
                                | _ -> 
                                    [MatchClause.Clause(j,k,firstResult); MatchClause.Clause(m,n,secondResult) ]
                        Expr.Match (f,g,h,i)
                    | e -> e

                | Expr.App (j,k,l,m), Expr.App _ -> 

                    if (containsExpr (function | Expr.MatchIsPlaceholder -> true | _ -> false) h) then 
                        let whenExpr = 
                            match l, whenExpr with 
                            | Expr.Ident x, [] -> [Some i] 
                            | Expr.Ident x, whenExpr when x = "op_BooleanAnd" -> whenExpr |> List.map (whenAnd (Some i)) 
                            | Expr.Ident x, whenExpr when x = "op_BooleanOr" -> Some i :: whenExpr

                        removeMatchPlaceholder h whenExpr firstResult secondResult (walker, a,b,c,d,e)
                    else 

                        let a = walker a
                        let b = walker b
                        let c = c |> Option.map walker
                        Expr.IfThenElse (a,b,c,d,e)

            | _ -> 
                //removeMatchPlaceholder c
                //,removeMatchPlaceholder s4)

                //rewriteMatchIs
                let a = walker a
                let b = walker b
                let c = c |> Option.map walker
                Expr.IfThenElse (a,b,c,d,e)
        

        let rec walker tree = 
            match tree with 
            | Expr.Sequential (s1,s2,s3,s4) -> 
                Expr.Sequential (s1,s2,walker s3,walker s4)

            | Expr.IfThenElse (a,b,c,d,e) -> 

                if (containsExpr (function | Expr.MatchIsPlaceholder -> true | _ -> false) a) then 
                    removeMatchPlaceholder a [] b c (walker, a,b,c,d,e)

                else 
                    let a = walker a
                    let b = walker b
                    let c = c |> Option.map walker
                    Expr.IfThenElse (a,b,c,d,e)

            | Expr.App (a,b,c,d) -> 

                let c = walker c
                let d = walker d
                Expr.App (a,b,c,d)

            | Expr.ForEach (a,b,c,d,e,f) -> 
                Expr.ForEach (a,b,c,d, walker e, walker f)
            | _ -> tree
        walker tree

    let wrapNewKeyword tree = 

        let rec wrapNewInParen tree = 
        
            let rec walker tree = 
                match tree with 
                | Expr.New (a,b,c) -> Expr.Paren (Expr.New (a,b,c))
                | Expr.Sequential (s1,s2,s3,s4) -> Expr.Sequential (s1,s2, walker s3, walker s4)
                | Expr.IfThenElse (a,b,c,d,e) -> 
                    let a = walker a
                    let b = walker b
                    let c = c |> Option.map walker
                    Expr.IfThenElse (a,b,c,d,e)
                | Expr.App (a,b,c,d) -> 

                    let c = walker c
                    let d = walker d
                    Expr.App (a,b,c,d)

                | Expr.LetOrUse (a,b,c,d) -> 
                    let mapping = 
                        c |> List.map (function 
                            | LetBind (a,b,c,d,e,f,g,h) -> 
                                LetBind (a,b,c,d,e,f,g,walker h) )
                    Expr.LetOrUse (a,b,mapping, walker d) 

                | Expr.ForEach (a,b,c,d,e,f) -> 
                    Expr.ForEach (a,b,c,d, walker e, walker f)

                | _ -> tree
            walker tree


        let rec walker tree = 
            match tree with
            | Expr.DotGet (a,b) -> Expr.DotGet (wrapNewInParen a,b)
            | Expr.Sequential (s1,s2,s3,s4) -> 
                match s3 with 
                | Expr.IfThenElse (x,y,z,i,j) when Option.isNone z -> 
                    Expr.IfThenElse (x,y, Some s4, i, j)

                | _ -> 
                    Expr.Sequential (s1,s2,walker s3,walker s4)

            | Expr.IfThenElse (a,b,c,d,e) -> 
                let a = walker a
                let b = walker b
                let c = c |> Option.map walker
                Expr.IfThenElse (a,b,c,d,e)

            | Expr.App (a,b,c,d) -> 

                let c = walker c
                let d = walker d
                Expr.App (a,b,c,d)

            | Expr.LetOrUse (a,b,c,d) -> 
                let mapping = 
                    c |> List.map (function 
                        | LetBind (a,b,c,d,e,f,g,h) -> 
                            LetBind (a,b,c,d,e,f,g,walker h) )

                Expr.LetOrUse (a,b,mapping, walker d) 

            | Expr.ForEach (a,b,c,d,e,f) -> 
                Expr.ForEach (a,b,c,d, walker e, walker f)
            | _ -> tree
        walker tree

    let replaceDotGetIfNotInLetBinding tree = 

        let rec walker tree = 
            match tree with
            | Expr.DotGet (a,b) -> 
                match a with 
                | Expr.LongIdent (c, prefixName) -> 
                    let b = joinLongIdentWithDots b
                    let prefixName = joinLongIdentWithDots prefixName
                    sprintf "%s.%s" prefixName b |> toLongIdent
                    
                | _ -> Expr.DotGet (a,b)
            
            | Expr.Sequential (s1,s2,s3,s4) -> 
                match s3 with 
                | Expr.IfThenElse (x,y,z,i,j) when Option.isNone z -> 
                    Expr.IfThenElse (x,y, Some s4, i, j)

                | _ -> 
                    Expr.Sequential (s1,s2,walker s3,walker s4)

            | Expr.IfThenElse (a,b,c,d,e) -> 
                let a = walker a
                let b = walker b
                let c = c |> Option.map walker
                Expr.IfThenElse (a,b,c,d,e)

            | Expr.App (a,b,c,d) -> 

                let c = walker c
                let d = walker d
                Expr.App (a,b,c,d)

            | Expr.ForEach (a,b,c,d,e,f) -> 
                Expr.ForEach (a,b,c,d, walker e, walker f)
            | _ -> tree
        walker tree

    let rec rewriteInLetExp tree = 

        let isLetPlaceholder exp = 
            match exp with 
            | Expr.InLetPlaceholder -> true
            | _ -> false

        match tree with 
        // root of tree should not be contain immeidate child of InLetPlaceholder (has no meaning)
        | Expr.LetOrUse (x,y,z,i) when isLetPlaceholder i |> not && y = true ->  
            Expr.LetOrUse (x,y,z,rewriteInLetExp i)


        // This will occur when 'invalid' C# is written. ie a value is assinged to a var, but the var is not used.
        // assume return type of unit.
        | Expr.LetOrUse (x,y,z,i) when isLetPlaceholder i ->  
            Expr.LetOrUse (x,y,z, Expr.Const SynConst.Unit)

        | Expr.Sequential (s1,s2,s3,s4) -> 
            match s3 with  
            | Expr.LetOrUse (x,y,z,i) when isLetPlaceholder i -> 
                let e = rewriteInLetExp s4 //|> addNewLine file
                Expr.LetOrUse (x,y,z,e)
            | _ -> 
                Expr.Sequential (s1,s2,rewriteInLetExp s3,rewriteInLetExp s4)

        | Expr.App (a,b,c,d) -> 

            match c with 
            | Expr.LetOrUse (x,y,z,i) when isLetPlaceholder i -> 
                let d = rewriteInLetExp d
                Expr.LetOrUse(x,y,z, d)
            | _ -> Expr.App (a,b, rewriteInLetExp c, rewriteInLetExp d)

        | Expr.IfThenElse (a,b,c,d,e) -> 
            //match b with 
            //| Expr.LetOrUse (x,y,z,i) when isLetPlaceholder i -> 
            //    Expr.LetOrUse(x,y,z, Expr.Const  SynConst.Unit)
            //| _ -> 
            Expr.IfThenElse (a,rewriteInLetExp b,c |> Option.map rewriteInLetExp,d,e)

        | Expr.ForEach (a,b,c,d,e,f) -> 
            let e2 = rewriteInLetExp e
            let f2 = rewriteInLetExp f

            Expr.ForEach (a,b,c,d, e2, f2)

        | Expr.Lambda (a,b,c,d) -> 
            Expr.Lambda (a,b,c, rewriteInLetExp d)

        | Expr.Paren e -> Expr.Paren (rewriteInLetExp e)

        | _ -> tree

    let rec rewriteMethodWithPrefix methodNames tree = 
        sprintf "MethodNames - %A" methodNames |> System.Diagnostics.Debug.WriteLine
        let recurse = rewriteMethodWithPrefix methodNames

        let isLetPlaceholder exp = 
            match exp with 
            | Expr.InLetPlaceholder -> true
            | _ -> false

        match tree with 
        | Expr.LongIdent (a, b) -> 
            sprintf "MethodNames: %A, b: %A" methodNames b |> System.Diagnostics.Debug.WriteLine
            if methodNames |> Seq.exists (fun name -> b.ToString().Contains name ) then 

                let idents = b.Lid |> List.map (fun x -> x.idText) |> String.concat "."
                Expr.LongIdent (a, idents |> sprintf "this.%s" |> toLongIdentWithDots )
            else Expr.LongIdent (a,b)
            
        // root of tree should not be contain immeidate child of InLetPlaceholder (has no meaning)
        | Expr.LetOrUse (x,y,z,i) ->  
            Expr.LetOrUse (x,y,z,recurse i)

        | Expr.Sequential (s1,s2,s3,s4) -> System.Diagnostics.Debug.WriteLine "Expr.Sequential"; Expr.Sequential (s1,s2,recurse s3,recurse s4)
        | Expr.App (a,b,c,d) -> System.Diagnostics.Debug.WriteLine "App"; Expr.App (a,b, recurse c, recurse d)
        | Expr.IfThenElse (a,b,c,d,e) -> Expr.IfThenElse (a,recurse b,c |> Option.map recurse,d,e)

        | Expr.ForEach (a,b,c,d,e,f) -> 
            let e2 = recurse e
            let f2 = recurse f

            Expr.ForEach (a,b,c,d, e2, f2)

        | _ -> tree

    let rec addNewLineToLet file tree = 
        let moveDown = Range.moveDownLine file
        let moveAndAdd r1 = r1 |> Range.moveDownLine file |> Range.addLine file

        let rec loop tree = 
            match tree with 
            | SynExpr.Paren (x, r1, p, r) -> SynExpr.Paren (x, moveDown r1, p, moveDown r)
            | SynExpr.Const (x, r1) -> SynExpr.Const (x, moveDown r1)
            | SynExpr.Typed (expr, typeName, r1) -> SynExpr.Typed (expr, typeName, moveDown r1)
            | SynExpr.Tuple (xs, rs, r)  -> SynExpr.Tuple (xs, rs, moveDown r)
            | SynExpr.App (a,b, c, d, r) -> SynExpr.App (a,b, loop c, loop d, moveDown r)
            | SynExpr.LetOrUse (a,b,c, d, r1) -> SynExpr.LetOrUse (a,b,c, d, moveDown r1)
            | SynExpr.Ident (r) -> SynExpr.Ident (Ident(r.idText, moveDown r.idRange))
            | SynExpr.LongIdent (a,b, c, r) -> SynExpr.LongIdent (a,b, c, moveDown r)
            | SynExpr.Set (left, right, r) -> SynExpr.Set (left, right, moveDown r)
            | SynExpr.Null r -> SynExpr.Null <| moveDown r
            | SynExpr.LongIdentSet (a,b,c) -> SynExpr.LongIdentSet (a,b, moveDown c)
            | SynExpr.Sequential (a,b,c,d, r1) -> 
                SynExpr.Sequential (a,b, loop  c, loop d,  moveAndAdd r1 )
            | SynExpr.IfThenElse (a, b, c,d, e, r1, r2) -> 

                SynExpr.IfThenElse (loop a, loop b, c |> Option.map (loop ), d,e, moveAndAdd r1, moveAndAdd r2)
            | SynExpr.ForEach (a,b,c,d,e,f,g) ->  SynExpr.ForEach (a,b,c,d,e,f,moveAndAdd g)
            | SynExpr.TypeApp (a,b,c,d,e,f,g) -> SynExpr.TypeApp (loop a, moveDown b,c,d,e, moveDown f, moveDown g)
            | SynExpr.DotGet (a,b,c, d) -> SynExpr.DotGet (a, moveDown b, c, moveDown d) 
            | SynExpr.FromParseError (a,b) -> SynExpr.FromParseError (loop a, moveAndAdd b)

        match tree with 
        | SynExpr.Sequential (s1,s2,s3,s4,s5) -> 
            let s3 = addNewLineToLet file s3
            let s4 = addNewLineToLet file s4
            SynExpr.Sequential (s1,s2, s3, s4, s5)

        | SynExpr.App (a,b,c,d,e) -> 
            let c = addNewLineToLet file c
            let d = addNewLineToLet file d
            SynExpr.App (a, b, c, d, e)

        | SynExpr.IfThenElse (a,b,c,d,e,f,g) -> 
            let a = addNewLineToLet file a
            let b = addNewLineToLet file b
            let c = c |> Option.map (addNewLineToLet file)
            SynExpr.IfThenElse (a, b, c, d, e, f,g)

        | SynExpr.ForEach (a,b,c,d,e,f,g) -> 
            SynExpr.ForEach (a,b,c,d, loop e |> addNewLineToLet file, loop f |> addNewLineToLet file, g)

        | SynExpr.LetOrUse (a,b,c,d,e) -> 
            let d = loop d |> addNewLineToLet file 
            SynExpr.LetOrUse (a,b,c, d, e)
        | _ -> tree

    // For some reason fantomas does not format let bindings correclty and wants to use the let .... in statements. 
    // This is done becuase there are not line endings. 
    // To correclty the problem, walk the tree and bump the line number, via the range, to add a new line. 
    // Fantomas will then print the let binding with out the in keyword, and instead, move to the next line. 
    let removeFhsarpIn file tree =

        match tree with
        | ParsedInput.ImplFile(implFile) ->
            // Extract declarations and walk over them
            let (ParsedImplFileInput(fn, script, name, a, b, modules, c)) = implFile

            let modules = 
                modules |> List.map (fun (SynModuleOrNamespace (a,b,c,modules,e,f,g,h)) -> 
                    let modules = 
                        modules |> List.map (fun x -> 
                            match x with 
                            | SynModuleDecl.Types (a,b) -> 
                                let a = 
                                    a |> List.map (fun (SynTypeDefn.TypeDefn (a,b,members,d)) -> 

                                        let b = 
                                            match b with 
                                            | SynTypeDefnRepr.ObjectModel (a,b,c) -> 
                                                let b = 
                                                    b |> List.map (fun x -> 
                                                        match x with 
                                                        | SynMemberDefn.Member ((Binding (a,b,c,d,e,f,g,h,i,expr,k,l)),m2) -> 
                                                            let m2 = Range.addLine file m2
                                                            let expr = expr |> addNewLineToLet file

                                                            SynMemberDefn.Member ((Binding (a,b,c,d,e,f,g,h,i,expr,k,l)),m2)

                                                        | x -> x
                                                    )

                                                SynTypeDefnRepr.ObjectModel (a,b,c)
                                            | x -> x

                                        let members = 
                                            members |> List.map (fun x -> 
                                                match x with 
                                                | SynMemberDefn.Member ((Binding (a,b,c,d,e,f,g,h,i,expr,k,l)),m2) -> 
                                                    let m2 = Range.addLine file m2
                                                    let expr = expr |> addNewLineToLet file

                                                    SynMemberDefn.Member ((Binding (a,b,c,d,e,f,g,h,i,expr,k,l)),m2)

                                                | x -> x
                                            )
                                        SynTypeDefn.TypeDefn (a,b,members,d)
                                    )
                                SynModuleDecl.Types (a,b)
                            | x -> x )
                    SynModuleOrNamespace (a,b,c,modules,e,f,g,h)
                )
            ParsedImplFileInput(fn, script, name, a, b, modules, c) |> ParsedInput.ImplFile
        | _ -> failwith "F# Interface file (*.fsi) not supported."