﻿// C# parser - this walks the CSharp syntax tree and creates the intermediate F# syntax tree
namespace FSharper.Core

open System
open Microsoft.FSharp.Compiler.Ast
open Microsoft.CodeAnalysis
open System.Linq
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.FSharp.Compiler
open Microsoft.FSharp.Compiler.Range

[<AutoOpen>]
module ParserUtil = 
    let toSingleIdent (s:string) = Ident(s, range0)

    let fixKeywords k = 
        match k with 
        | "object" -> "obj"
        | x -> x

    let toIdent (s:string) = 
        s.Split('.') 
        |> Array.toList
        |> List.map toSingleIdent

    let toLongIdentWithDots (s:string) = 
        LongIdentWithDots (toIdent s, [range0])

    let toLongIdent (s:string) = 
        Expr.LongIdent (false, toLongIdentWithDots s)

    let sequential xs = 
        match xs |> Seq.toList with
        | _::_::_ as xs ->
            // List is built in reverse order to preserve the structure allowing for transformatins
            xs |> Seq.rev |> Seq.reduce (fun a b -> Expr.Sequential (SequencePointsAtSeq, true, b, a)) 
        | x::[] -> x
        | [] -> Expr.Const SynConst.Unit

    let joinLongIdentWithDots (b:LongIdentWithDots) = 
        b.Lid |> List.map (fun x -> x.idText) |> String.concat "." 

    let mapLongIdent f ident = 
        match ident with 
        | Expr.LongIdent (a,b) -> b |> joinLongIdentWithDots |> f |> toLongIdent
        | x -> x

    let toPatId ident = 
        SynSimplePat.Id (Ident(ident, range0), None, false, true, false, range0)

    let parseType (t: TypeSyntax) = 
        match t with 
        | :? NameSyntax as n -> 
            match n with 
            | :? GenericNameSyntax as genericName -> 
                let typeArgs = 
                    genericName.TypeArgumentList.Arguments
                    |> Seq.map (fun x -> x.WithoutTrivia().ToString())
                    |> Seq.map (toLongIdentWithDots >> SynType.LongIdent)
                    |> Seq.toList

                let x = genericName.Identifier.WithoutTrivia().ToFullString() |> toLongIdentWithDots |> SynType.LongIdent

                SynType.App  (x, None, typeArgs, [], None, false, range0)

            | _ -> n.WithoutTrivia().ToString() |> fixKeywords |> toLongIdentWithDots |> SynType.LongIdent
        | x -> x.WithoutTrivia().ToString() |> fixKeywords |> toLongIdentWithDots |> SynType.LongIdent 
    

[<AutoOpen>]
module SimpleFormatter = 

    type Config = Debug | Release

    let config = Debug

    let debugFormat s f : Expr = 
        match config with 
        | Debug -> s |> toLongIdent
        | Release -> f () 

type CSharpStatementWalker() = 

    static member ToCsharpSyntaxNode (node:SyntaxNode) =
        match node with
        | :? CSharpSyntaxNode as x -> Some x
        | _ -> None

    static member ParseAssignmentExpressionSyntax (node:AssignmentExpressionSyntax): Expr = 

        match node.Kind() with

        | SyntaxKind.SubtractAssignmentExpression -> 
            node.WithoutTrivia().ToFullString() |> Expr.Ident

        | SyntaxKind.SimpleAssignmentExpression -> 
            let right = node.Right |> CSharpStatementWalker.ParseExpression

            let l = LongIdentWithDots (node.Left.WithoutTrivia().ToFullString() |> toIdent, [range0])
            Expr.LongIdentSet (l, right)


        | SyntaxKind.AddAssignmentExpression -> 
            
            let isPlusEquals = 
                node.ChildTokens() |> Seq.exists (fun x -> x.Kind() = SyntaxKind.PlusEqualsToken)

            let left = 
                node.Left |> CSharpStatementWalker.ParseChsarpNode
            let right = node.Right |> CSharpStatementWalker.ParseChsarpNode

            if isPlusEquals then 
                match right with 
                | Expr.Lambda _ -> 

                    let ident = node.Left.WithoutTrivia().ToFullString() |> toLongIdent
                    let app = Expr.DotGet (ident, toLongIdentWithDots "AddHandler" )
                    let app = Expr.TypeApp (app, [SynType.Anon range0])
                    Expr.App (ExprAtomicFlag.Atomic, false, app, Expr.Paren right)
                | _ -> 
                    let addOp = PrettyNaming.CompileOpName "+"
                    let add = 
                        Expr.App (ExprAtomicFlag.NonAtomic, false, 
                            Expr.App(ExprAtomicFlag.NonAtomic, true, Expr.Ident addOp, left), right) 
                    Expr.Set (left, add)
            else
                Expr.Set (left, right)


        | _ -> node.WithoutTrivia().ToFullString() |> Expr.Ident

    static member ParseBinaryExpresson (node:BinaryExpressionSyntax):Expr = 

        let createLogicalExpression join = 

            let left = 
                match node.Left with 
                | :? BinaryExpressionSyntax as x -> CSharpStatementWalker.ParseBinaryExpresson x 
                | _ -> CSharpStatementWalker.ParseChsarpNode node.Left

            let right = 
                match node.Right with 
                | :? BinaryExpressionSyntax as x -> CSharpStatementWalker.ParseBinaryExpresson x 
                | _ -> CSharpStatementWalker.ParseChsarpNode node.Right

            Expr.App (ExprAtomicFlag.NonAtomic, false, 
                Expr.App(ExprAtomicFlag.NonAtomic, true, Expr.Ident join, left), right) 

        match node.Kind() with 
        | SyntaxKind.AddExpression -> PrettyNaming.CompileOpName "+" |> createLogicalExpression
        | SyntaxKind.LogicalAndExpression -> PrettyNaming.CompileOpName "&&" |> createLogicalExpression
        | SyntaxKind.LogicalOrExpression -> PrettyNaming.CompileOpName "||" |> createLogicalExpression 
        | SyntaxKind.NotEqualsExpression -> PrettyNaming.CompileOpName "<>" |> createLogicalExpression 
        | SyntaxKind.EqualsExpression -> PrettyNaming.CompileOpName "=" |> createLogicalExpression 
        | SyntaxKind.GreaterThanOrEqualExpression -> PrettyNaming.CompileOpName ">=" |> createLogicalExpression 
        | SyntaxKind.GreaterThanExpression -> PrettyNaming.CompileOpName ">" |> createLogicalExpression 
        | SyntaxKind.LessThanExpression -> PrettyNaming.CompileOpName "<" |> createLogicalExpression 
        | SyntaxKind.LessThanOrEqualExpression -> PrettyNaming.CompileOpName "<=" |> createLogicalExpression 
        | SyntaxKind.AsExpression -> 

            let left = 
                match node.Left with 
                | _ -> CSharpStatementWalker.ParseChsarpNode node.Left

            let right = 
                match node.Right with 
                | :? IdentifierNameSyntax as x -> x.Identifier.ValueText |> toLongIdentWithDots |>  SynType.LongIdent
                | x -> sprintf "Expected type identifier, but got: %A" x |> failwith

            Expr.Downcast (left, right)

    static member ParseToken (node:SyntaxToken) = 

        match node.Kind() with 
        | SyntaxKind.ExclamationToken -> "!" |> PrettyNaming.CompileOpName |> Expr.Ident
        | SyntaxKind.EqualsGreaterThanToken -> ">=" |> PrettyNaming.CompileOpName |> Expr.Ident
        | SyntaxKind.EqualsToken -> "=" |> PrettyNaming.CompileOpName |> Expr.Ident
        | SyntaxKind.GreaterThanToken -> ">" |> PrettyNaming.CompileOpName |> Expr.Ident
        | SyntaxKind.LessThanToken -> "<" |> PrettyNaming.CompileOpName |> Expr.Ident
        | SyntaxKind.LessThanEqualsToken -> "<=" |> PrettyNaming.CompileOpName |> Expr.Ident
        | SyntaxKind.SemicolonToken-> ";" |> PrettyNaming.CompileOpName |> Expr.Ident
        | SyntaxKind.NotEqualsExpression-> "!=" |> PrettyNaming.CompileOpName |> Expr.Ident
        | SyntaxKind.NumericLiteralToken -> 

            let s = node.Text
            let asInt = 
                match Int32.TryParse s with 
                | true, x -> Some x 
                | false,_ ->  None 

            let asInt64 = 
                match Int64.TryParse s with 
                | true, x -> Some x 
                | false,_ ->  None 

            let asfloat = 
                match Double.TryParse s with 
                | true, x -> Some x 
                | false,_ ->  None 

            match asInt, asInt64, asfloat with 
            | Some x, _, _ ->  Expr.Const <| SynConst.Int32 x
            | _, Some x, _ ->  Expr.Const <| SynConst.Int64 x
            | _, _, Some x ->  Expr.Const <| SynConst.Double x
            | _, _, _  ->  toLongIdent s

        | SyntaxKind.IdentifierToken -> node.WithoutTrivia().ToFullString() |> toLongIdent
        //| SyntaxKind.ReturnKeyword -> Expr.Const SynConst.Unit
        //| SyntaxKind.ForEachKeyword -> "for" |> toLongIdent

        | SyntaxKind.BoolKeyword -> "Bool" |> toLongIdent
        | SyntaxKind.ByteKeyword -> "Byte" |> toLongIdent
        | SyntaxKind.SByteKeyword -> "SByte" |> toLongIdent
        | SyntaxKind.ShortKeyword -> "Short" |> toLongIdent
        | SyntaxKind.UShortKeyword -> "UShort" |> toLongIdent
        | SyntaxKind.IntKeyword -> "Int" |> toLongIdent
        | SyntaxKind.UIntKeyword -> "UInt" |> toLongIdent
        | SyntaxKind.LongKeyword -> "Long" |> toLongIdent
        | SyntaxKind.ULongKeyword -> "ULong" |> toLongIdent
        | SyntaxKind.DoubleKeyword -> "Double" |> toLongIdent
        | SyntaxKind.FloatKeyword -> "Float" |> toLongIdent
        | SyntaxKind.DecimalKeyword -> "Decimal" |> toLongIdent
        | SyntaxKind.StringKeyword -> "String" |> toLongIdent
        | SyntaxKind.CharKeyword -> "Char" |> toLongIdent
        | SyntaxKind.VoidKeyword -> "Unit" |> toLongIdent
        | SyntaxKind.ObjectKeyword -> "obj" |> toLongIdent
        | SyntaxKind.TypeOfKeyword -> "TypeOf" |> toLongIdent
        | SyntaxKind.SizeOfKeyword -> "SizeOf" |> toLongIdent
        | SyntaxKind.NullKeyword -> Expr.Null
        | SyntaxKind.TrueKeyword -> "True" |> toLongIdent
        | SyntaxKind.FalseKeyword -> "False" |> toLongIdent
        | SyntaxKind.IfKeyword -> "If" |> toLongIdent
        | SyntaxKind.ElseKeyword -> "Else" |> toLongIdent
        | SyntaxKind.WhileKeyword -> "While" |> toLongIdent
        | SyntaxKind.ForKeyword -> "For" |> toLongIdent
        | SyntaxKind.ForEachKeyword -> "ForEach" |> toLongIdent
        | SyntaxKind.DoKeyword -> "Do" |> toLongIdent
        | SyntaxKind.SwitchKeyword -> "Switch" |> toLongIdent
        | SyntaxKind.CaseKeyword -> "Case" |> toLongIdent
        | SyntaxKind.DefaultKeyword -> "Default" |> toLongIdent
        | SyntaxKind.TryKeyword -> "Try" |> toLongIdent
        | SyntaxKind.CatchKeyword -> "Catch" |> toLongIdent
        | SyntaxKind.FinallyKeyword -> "Finally" |> toLongIdent
        | SyntaxKind.LockKeyword -> "Lock" |> toLongIdent
        | SyntaxKind.GotoKeyword -> "Goto" |> toLongIdent
        | SyntaxKind.BreakKeyword -> "Break" |> toLongIdent
        | SyntaxKind.ContinueKeyword -> "Continue" |> toLongIdent
        //| SyntaxKind.ReturnKeyword -> Expr.ReturnFromIf
        | SyntaxKind.ThrowKeyword -> "Throw" |> toLongIdent
        | SyntaxKind.PublicKeyword -> "Public" |> toLongIdent
        | SyntaxKind.PrivateKeyword -> "Private" |> toLongIdent
        | SyntaxKind.InternalKeyword -> "Internal" |> toLongIdent
        | SyntaxKind.ProtectedKeyword -> "Protected" |> toLongIdent
        | SyntaxKind.StaticKeyword -> "Static" |> toLongIdent
        | SyntaxKind.ReadOnlyKeyword -> "ReadOnly" |> toLongIdent
        | SyntaxKind.SealedKeyword -> "Sealed" |> toLongIdent
        | SyntaxKind.ConstKeyword -> "Const" |> toLongIdent
        | SyntaxKind.FixedKeyword -> "Fixed" |> toLongIdent
        | SyntaxKind.StackAllocKeyword -> "StackAlloc" |> toLongIdent
        | SyntaxKind.VolatileKeyword -> "Volatile" |> toLongIdent
        | SyntaxKind.NewKeyword -> "New" |> toLongIdent
        | SyntaxKind.OverrideKeyword -> "Override" |> toLongIdent
        | SyntaxKind.AbstractKeyword -> "Abstract" |> toLongIdent
        | SyntaxKind.VirtualKeyword -> "Virtual" |> toLongIdent
        | SyntaxKind.EventKeyword -> "Event" |> toLongIdent
        | SyntaxKind.ExternKeyword -> "Extern" |> toLongIdent
        | SyntaxKind.RefKeyword -> "Ref" |> toLongIdent
        | SyntaxKind.OutKeyword -> "Out" |> toLongIdent
        | SyntaxKind.InKeyword -> "In" |> toLongIdent
        | SyntaxKind.IsKeyword -> "Is" |> toLongIdent
        | SyntaxKind.AsKeyword -> "As" |> toLongIdent
        | SyntaxKind.ParamsKeyword -> "Params" |> toLongIdent
        | SyntaxKind.ArgListKeyword -> "ArgList" |> toLongIdent
        | SyntaxKind.MakeRefKeyword -> "MakeRef" |> toLongIdent
        | SyntaxKind.RefTypeKeyword -> "RefType" |> toLongIdent
        | SyntaxKind.RefValueKeyword -> "RefValue" |> toLongIdent
        | SyntaxKind.ThisKeyword -> "This" |> toLongIdent
        | SyntaxKind.BaseKeyword -> "Base" |> toLongIdent
        | SyntaxKind.NamespaceKeyword -> "Namespace" |> toLongIdent
        //| SyntaxKind.UsingKeyword -> "Using" |> toLongIdent
        | SyntaxKind.ClassKeyword -> "Class" |> toLongIdent
        | SyntaxKind.StructKeyword -> "Struct" |> toLongIdent
        | SyntaxKind.InterfaceKeyword -> "Interface" |> toLongIdent
        | SyntaxKind.EnumKeyword -> "Enum" |> toLongIdent
        | SyntaxKind.DelegateKeyword -> "Delegate" |> toLongIdent
        | SyntaxKind.CheckedKeyword -> "Checked" |> toLongIdent
        | SyntaxKind.UncheckedKeyword -> "Unchecked" |> toLongIdent
        | SyntaxKind.UnsafeKeyword -> "Unsafe" |> toLongIdent
        | SyntaxKind.OperatorKeyword -> "Operator" |> toLongIdent
        | SyntaxKind.ExplicitKeyword -> "Explicit" |> toLongIdent
        | SyntaxKind.ImplicitKeyword -> "Implicit" |> toLongIdent
        | SyntaxKind.YieldKeyword -> "Yield" |> toLongIdent
        | SyntaxKind.PartialKeyword -> "Partial" |> toLongIdent
        | SyntaxKind.AliasKeyword -> "Alias" |> toLongIdent
        | SyntaxKind.GlobalKeyword -> "Global" |> toLongIdent
        | SyntaxKind.AssemblyKeyword -> "Assembly" |> toLongIdent
        | SyntaxKind.ModuleKeyword -> "Module" |> toLongIdent
        | SyntaxKind.TypeKeyword -> "Type" |> toLongIdent
        | SyntaxKind.FieldKeyword -> "Field" |> toLongIdent
        | SyntaxKind.MethodKeyword -> "Method" |> toLongIdent
        | SyntaxKind.ParamKeyword -> "Param" |> toLongIdent
        | SyntaxKind.PropertyKeyword -> "Property" |> toLongIdent
        | SyntaxKind.TypeVarKeyword -> "TypeVar" |> toLongIdent
        | SyntaxKind.GetKeyword -> "Get" |> toLongIdent
        | SyntaxKind.SetKeyword -> "Set" |> toLongIdent
        | SyntaxKind.AddKeyword -> "Add" |> toLongIdent
        | SyntaxKind.RemoveKeyword -> "Remove" |> toLongIdent
        | SyntaxKind.WhereKeyword -> "Where" |> toLongIdent
        | SyntaxKind.FromKeyword -> "From" |> toLongIdent
        | SyntaxKind.GroupKeyword -> "Group" |> toLongIdent
        | SyntaxKind.JoinKeyword -> "Join" |> toLongIdent
        | SyntaxKind.IntoKeyword -> "Into" |> toLongIdent
        | SyntaxKind.LetKeyword -> "Let" |> toLongIdent
        | SyntaxKind.ByKeyword -> "By" |> toLongIdent
        | SyntaxKind.SelectKeyword -> "Select" |> toLongIdent
        | SyntaxKind.OrderByKeyword -> "OrderBy" |> toLongIdent
        | SyntaxKind.OnKeyword -> "On" |> toLongIdent
        | SyntaxKind.EqualsKeyword -> "Equals" |> toLongIdent
        | SyntaxKind.AscendingKeyword -> "Ascending" |> toLongIdent
        | SyntaxKind.DescendingKeyword -> "Descending" |> toLongIdent
        | SyntaxKind.NameOfKeyword -> "NameOf" |> toLongIdent
        | SyntaxKind.AsyncKeyword -> "Async" |> toLongIdent
        | SyntaxKind.AwaitKeyword -> "Await" |> toLongIdent
        | SyntaxKind.WhenKeyword -> "When" |> toLongIdent
        | SyntaxKind.ElifKeyword -> "Elif" |> toLongIdent
        | SyntaxKind.EndIfKeyword -> "EndIf" |> toLongIdent
        | SyntaxKind.RegionKeyword -> "Region" |> toLongIdent
        | SyntaxKind.EndRegionKeyword -> "EndRegion" |> toLongIdent
        | SyntaxKind.DefineKeyword -> "Define" |> toLongIdent
        | SyntaxKind.UndefKeyword -> "Undef" |> toLongIdent
        | SyntaxKind.WarningKeyword -> "Warning" |> toLongIdent
        | SyntaxKind.ErrorKeyword -> "Error" |> toLongIdent
        | SyntaxKind.LineKeyword -> "Line" |> toLongIdent
        | SyntaxKind.PragmaKeyword -> "Pragma" |> toLongIdent
        | SyntaxKind.HiddenKeyword -> "Hidden" |> toLongIdent
        | SyntaxKind.ChecksumKeyword -> "Checksum" |> toLongIdent
        | SyntaxKind.DisableKeyword -> "Disable" |> toLongIdent
        | SyntaxKind.RestoreKeyword -> "Restore" |> toLongIdent
        | SyntaxKind.ReferenceKeyword -> "Reference" |> toLongIdent
        | SyntaxKind.LoadKeyword -> "Load" |> toLongIdent
        | SyntaxKind.StringLiteralToken -> 
            (node.ValueText, range0) |> SynConst.String |> Expr.Const 

            //| SyntaxKind.CharacterLiteralToken -> Line ""
            //| _ -> node.ValueText |> Line

    static member ParseInterpolatedStringContentSyntax (node:InterpolatedStringContentSyntax) = 
        match node with 
        //| :? InterpolatedStringTextSyntax as x -> "-" + x.TextToken.Text |> Line
        | :? InterpolationSyntax as x -> x.Expression |> CSharpStatementWalker.ParseChsarpNode |> Expr.Paren
        | :? InterpolatedStringTextSyntax as x -> Expr.Const (SynConst.String (x.WithoutTrivia().ToString(), range0))
        //| x -> x.ToFullString() |> Line

    static member ParseStatementSyntax (node:StatementSyntax) = 
        match node with 
        | :? BlockSyntax as x -> x.Statements  |> Seq.map CSharpStatementWalker.ParseStatementSyntax |> sequential
        | :? BreakStatementSyntax as x -> Expr.ReturnFromIf <| Expr.Const SynConst.Unit // "BreakStatement" |> toLongIdent
        | :? CheckedStatementSyntax as x -> "CheckedStatement" |> toLongIdent
        | :? CommonForEachStatementSyntax as x -> 
            match x with 
            | :? ForEachStatementSyntax as x -> 

                let var = Pat.Named (Pat.Wild, Ident(x.Identifier.ValueText, range0), false, None)
                let exp = x.Expression |> CSharpStatementWalker.ParseChsarpNode
                let body = x.Statement |> CSharpStatementWalker.ParseChsarpNode
                Expr.ForEach (SequencePointInfoForForLoop.SequencePointAtForLoop range0, SeqExprOnly.SeqExprOnly false, true, var, exp, body)

            | :? ForEachVariableStatementSyntax as x -> 

                let v = x.Variable |> CSharpStatementWalker.ParseChsarpNode
                let var = 
                    let rec toPat v = 
                        match v with 
                        | Expr.Ident x -> Pat.Named (Pat.Wild, Ident(x, range0), false, None)
                        | Expr.LongIdent (a,b) -> Pat.LongIdent (b,None, None, SynConstructorArgs.Pats [], None)
                        | Expr.Tuple xs -> xs |> List.map toPat |> Pat.Tuple
                        | _ -> failwith "Not valid for Pat"
                    toPat v

                let exp = x.Expression |> CSharpStatementWalker.ParseChsarpNode
                let body = x.Statement |> CSharpStatementWalker.ParseChsarpNode

                Expr.ForEach (SequencePointInfoForForLoop.SequencePointAtForLoop range0, SeqExprOnly.SeqExprOnly false, true, var, exp, body)

        | :? ContinueStatementSyntax as x -> Expr.ReturnFromIf <| Expr.Const SynConst.Unit
        | :? DoStatementSyntax as x -> "DoStatement" |> toLongIdent
        | :? EmptyStatementSyntax as x -> "EmptyStatement" |> toLongIdent
        | :? ExpressionStatementSyntax as x ->
            x.Expression |> CSharpStatementWalker.ParseExpression
        | :? FixedStatementSyntax as x -> "FixedStatement" |> toLongIdent
        | :? ForStatementSyntax as x -> "ForStatement" |> toLongIdent
        | :? GotoStatementSyntax as x -> "GotoStatement" |> toLongIdent
        | :? IfStatementSyntax as x ->
            let condtion = CSharpStatementWalker.ParseChsarpNode x.Condition
            let statement = CSharpStatementWalker.ParseChsarpNode x.Statement
            let elseExpr = x.Else |>  Option.ofObj |> Option.map CSharpStatementWalker.ParseChsarpNode
            printfn "elseExpr: %A" elseExpr
            Expr.IfThenElse (condtion, statement, elseExpr, SequencePointInfoForBinding.SequencePointAtBinding range0, false)

        | :? LabeledStatementSyntax as x -> "LabeledStatement" |> toLongIdent
        | :? LocalDeclarationStatementSyntax as x -> 
            x.Declaration |> CSharpStatementWalker.ParseChsarpNode
        | :? LocalFunctionStatementSyntax as x -> "LocalFunctionStatement" |> toLongIdent
        | :? LockStatementSyntax as x -> "LockStatement" |> toLongIdent
        | :? ReturnStatementSyntax as x -> 
            if x.Expression = null then 
                Expr.Const SynConst.Unit
            else 
                CSharpStatementWalker.ParseExpression x.Expression |> Expr.ReturnFromIf
        | :? SwitchStatementSyntax as x -> "SwitchStatement" |> toLongIdent
        | :? ThrowStatementSyntax as x -> "ThrowStatement" |> toLongIdent
        | :? TryStatementSyntax as x -> "TryStatement" |> toLongIdent
        | :? UnsafeStatementSyntax as x -> "UnsafeStatement" |> toLongIdent
        | :? UsingStatementSyntax as x -> 

            let var = 
                FSharpBinding.LetBind 
                    (
                        None, SynBindingKind.NormalBinding, false, false, [], 
                        SynValData (None, SynValInfo ([], SynArgInfo ([], false, None )), None), 
                        Pat.Named (Pat.Wild, Ident("_", range0), false, None), 
                        x.Expression |> CSharpStatementWalker.ParseChsarpNode)
            let s = x.Statement |> CSharpStatementWalker.ParseChsarpNode

            Expr.LetOrUse (false, true, [var], s)

        | :? WhileStatementSyntax as x -> "WhileStatement" |> toLongIdent
        | :? YieldStatementSyntax as x -> "YieldStatement" |> toLongIdent

    static member ParseChsarpNode (node:CSharpSyntaxNode, ?withSelfIdentifier):Expr = 


        match node with 

        | null -> Expr.Null
        | :? IdentifierNameSyntax as x -> 
            x.Identifier.WithoutTrivia().ToFullString() |> toLongIdent

        | :? AccessorDeclarationSyntax as x -> "AccessorDeclaration" |> toLongIdent
        | :? AccessorListSyntax as x -> "AccessorList" |> toLongIdent
        | :? AnonymousObjectMemberDeclaratorSyntax as x -> "AnonymousObjectMemberDeclarator" |> toLongIdent
        | :? ArgumentSyntax as x -> x.Expression |> CSharpStatementWalker.ParseExpression
        | :? ArrayRankSpecifierSyntax as x -> "ArrayRankSpecifier" |> toLongIdent
        | :? ArrowExpressionClauseSyntax as x -> "ArrowExpressionClause" |> toLongIdent
        | :? AttributeArgumentListSyntax as x -> 
            x.Arguments |> Seq.toList 
            |> List.map (fun x -> CSharpStatementWalker.ParseChsarpNode (x, ?withSelfIdentifier = withSelfIdentifier))
            |> Expr.Tuple 
            |> Expr.Paren

        | :? AttributeArgumentSyntax as x -> CSharpStatementWalker.ParseExpression (x.Expression)
        | :? AttributeListSyntax as x -> "AttributeList" |> toLongIdent
        | :? AttributeSyntax as x -> "Attribute" |> toLongIdent
        | :? AttributeTargetSpecifierSyntax as x -> "AttributeTargetSpecifier" |> toLongIdent
        | :? BaseArgumentListSyntax as x -> 
            x.Arguments |> Seq.map (fun x -> CSharpStatementWalker.ParseChsarpNode (x.Expression, ?withSelfIdentifier = withSelfIdentifier)) 
            |> Seq.toList |> Expr.Tuple |> Expr.Paren

            //"BaseArgumentList" |> toLongIdent
        | :? BaseCrefParameterListSyntax as x -> "BaseCrefParameterList" |> toLongIdent
        | :? BaseListSyntax as x -> "BaseList" |> toLongIdent
        | :? BaseParameterListSyntax as x -> "BaseParameterList" |> toLongIdent
        | :? BaseTypeSyntax as x -> 

            printfn "| :? BaseTypeSyntax"
            printfn "%A" x
            printfn "%A" <| x.Kind()

            "BaseType" |> toLongIdent
        | :? CatchClauseSyntax as x -> "CatchClause" |> toLongIdent
        | :? CatchDeclarationSyntax as x -> "CatchDeclaration" |> toLongIdent
        | :? CatchFilterClauseSyntax as x -> "CatchFilterClause" |> toLongIdent
        | :? CompilationUnitSyntax as x -> "CompilationUnit" |> toLongIdent
        | :? ConstructorInitializerSyntax as x -> "ConstructorInitializer" |> toLongIdent
        | :? CrefParameterSyntax as x -> "CrefParameter" |> toLongIdent
        | :? CrefSyntax as x -> "Cref" |> toLongIdent
        | :? ElseClauseSyntax as x -> 
            printfn "ElseClause: %A" x.Statement
            x.Statement |> CSharpStatementWalker.ParseStatementSyntax
        | :? EqualsValueClauseSyntax as x -> x.Value |> CSharpStatementWalker.ParseExpression
        | :? ExplicitInterfaceSpecifierSyntax as x -> "ExplicitInterfaceSpecifier" |> toLongIdent
        | :? ExpressionSyntax as x -> CSharpStatementWalker.ParseExpression (x)
        | :? ExternAliasDirectiveSyntax as x -> "ExternAliasDirective" |> toLongIdent
        | :? FinallyClauseSyntax as x -> "FinallyClause" |> toLongIdent
        | :? InterpolatedStringContentSyntax as x -> "InterpolatedStringContent" |> toLongIdent
        | :? InterpolationAlignmentClauseSyntax as x -> "InterpolationAlignmentClause" |> toLongIdent
        | :? InterpolationFormatClauseSyntax as x -> "InterpolationFormatClause" |> toLongIdent
        | :? JoinIntoClauseSyntax as x -> "JoinIntoClause" |> toLongIdent
        | :? MemberDeclarationSyntax as x -> "MemberDeclaration" |> toLongIdent
        | :? NameColonSyntax as x -> "NameColon" |> toLongIdent
        | :? NameEqualsSyntax as x -> "NameEquals" |> toLongIdent
        | :? OrderingSyntax as x -> "Ordering" |> toLongIdent
        | :? ParameterSyntax as x -> "Parameter" |> toLongIdent
        | :? PatternSyntax as x -> "Pattern" |> toLongIdent
        | :? QueryBodySyntax as x -> "QueryBody" |> toLongIdent
        | :? QueryClauseSyntax as x -> "QueryClause" |> toLongIdent
        | :? QueryContinuationSyntax as x -> "QueryContinuation" |> toLongIdent
        | :? SelectOrGroupClauseSyntax as x -> "SelectOrGroupClause" |> toLongIdent
        | :? StatementSyntax as x -> x |> CSharpStatementWalker.ParseStatementSyntax
        | :? StructuredTriviaSyntax as x -> "StructuredTrivia" |> toLongIdent
        | :? SwitchLabelSyntax as x -> "SwitchLabel" |> toLongIdent
        | :? SwitchSectionSyntax as x -> "SwitchSection" |> toLongIdent
        | :? TupleElementSyntax as x -> "TupleElement" |> toLongIdent
        | :? TypeArgumentListSyntax as x -> "TypeArgumentList" |> toLongIdent
        | :? TypeParameterConstraintClauseSyntax as x -> "TypeParameterConstraintClause" |> toLongIdent
        | :? TypeParameterConstraintSyntax as x -> "TypeParameterConstraint" |> toLongIdent
        | :? TypeParameterListSyntax as x -> "TypeParameterList" |> toLongIdent
        | :? TypeParameterSyntax as x -> "TypeParameter" |> toLongIdent
        //| :? UsingDirectiveSyntax as x ->  
            //x.Name |> 
            //x.Name
             //"UsingDirective" |> toLongIdent
        | :? VariableDeclarationSyntax as x -> 

            x.Variables 
            |> Seq.map CSharpStatementWalker.ParseChsarpNode 
            |> sequential
        | :? VariableDeclaratorSyntax as x -> 

            let init = x.Initializer |> CSharpStatementWalker.ParseChsarpNode
            Expr.LetOrUse(
                false, false, 
                [LetBind(None, SynBindingKind.NormalBinding, false, true, [],
                    SynValData (
                        None, SynValInfo ([], SynArgInfo ([], false, None )), None), 
                    Pat.Named (Pat.Wild, Ident(x.Identifier.ValueText, range0), false, None), init)], Expr.InLetPlaceholder)

        //| :? VariableDesignationSyntax as x -> x.with//"VariableDesignation" |> toLongIdent
        | :? WhenClauseSyntax as x -> "WhenClause" |> toLongIdent
        | :? XmlAttributeSyntax as x -> "XmlAttribute" |> toLongIdent
        | :? XmlElementEndTagSyntax as x -> "XmlElementEndTag" |> toLongIdent
        | :? XmlElementStartTagSyntax as x -> "XmlElementStartTag" |> toLongIdent
        | :? XmlNameSyntax as x -> "XmlName" |> toLongIdent
        | :? XmlNodeSyntax as x -> "XmlNode" |> toLongIdent
        | :? XmlPrefixSyntax as x -> "XmlPrefix" |> toLongIdent

    static member ParseExpression (node:ExpressionSyntax) = 

        if node = null then  printfn "Node was null"; Expr.Const SynConst.Unit else

        match node with
        | :? IsPatternExpressionSyntax as x -> 
            let expr = CSharpStatementWalker.ParseExpression x.Expression
            printfn "ClauseE:\n%A" expr
            printfn "ClauseP:\n%A" x.Pattern
            let clause = 
                match x.Pattern with 
                | :? DeclarationPatternSyntax as x -> 

                    let t = x.Type |> ParserUtil.parseType
                    let name = x.Designation.WithoutTrivia().ToString() |> toSingleIdent
                    MatchClause.Clause (SynPat.Named (SynPat.IsInst (t,range0), name, false, None, range0), None, Expr.MatchIsPlaceholder)
                //| :? ConstantPatternSyntax as x -> x.Expression

            printfn "Clause:\n%A" clause
            Expr.Match (SequencePointInfoForBinding.SequencePointAtBinding range0, expr, [clause], false)

        | :? IdentifierNameSyntax as x -> CSharpStatementWalker.ParseChsarpNode x
        | :? AnonymousFunctionExpressionSyntax as x -> 
            match x with 
            | :? LambdaExpressionSyntax as x -> 
                match x with 
                | :? SimpleLambdaExpressionSyntax as x -> 
                    let b = x.Body |> CSharpStatementWalker.ParseChsarpNode
                    let n = 
                        SynSimplePats.SimplePats ([
                            SynSimplePat.Id 
                                (Ident(x.Parameter.Identifier.ValueText, range0), 
                                    None, false, true, false, range0)] , range0)

                    Expr.Lambda (true, false, n, b)
                | :? ParenthesizedLambdaExpressionSyntax as x -> 

                    let args = 
                        let idents = 
                            if x.ParameterList = null then []
                            else 
                                x.ParameterList.Parameters 
                                |> Seq.map (fun x -> toPatId x.Identifier.ValueText)
                                |> Seq.toList
                        SynSimplePats.SimplePats (idents, range0)

                    let body = x.Body |> CSharpStatementWalker.ParseChsarpNode
                    Expr.Lambda (true, false, args, body)

            | :? AnonymousMethodExpressionSyntax as x -> 

                let args = 
                    let idents = 
                        if x.ParameterList = null then []
                        else 
                            x.ParameterList.Parameters 
                            |> Seq.map (fun x -> toPatId x.Identifier.ValueText)
                            |> Seq.toList
                    SynSimplePats.SimplePats (idents, range0)

                let body = x.Body |> CSharpStatementWalker.ParseChsarpNode
                Expr.Lambda (true, false, args, body)
                

        | :? AnonymousObjectCreationExpressionSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "AnonymousObjectCreationExpressionSyntax"
        | :? ArrayCreationExpressionSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "ArrayCreationExpressionSyntax"
        | :? AssignmentExpressionSyntax as x -> x |> CSharpStatementWalker.ParseAssignmentExpressionSyntax
        | :? AwaitExpressionSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "AwaitExpressionSyntax"
        | :? BinaryExpressionSyntax as x -> x |> CSharpStatementWalker.ParseBinaryExpresson
        | :? CastExpressionSyntax as x -> 
            printfn "CastExpressionSyntax: %b" (x.Expression = null)
            let exp = CSharpStatementWalker.ParseExpression x.Expression
            let castType = ParserUtil.parseType x.Type
            Expr.Downcast (exp, castType)

        | :? CheckedExpressionSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "CheckedExpressionSyntax"
        | :? ConditionalAccessExpressionSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "ConditionalAccessExpressionSyntax"
        | :? ConditionalExpressionSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "ConditionalExpressionSyntax" 

        //| :? DeclarationExpressionSyntax as x -> x.Designation x.Type |> ParserUtil.parseType
        | :? DefaultExpressionSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "DefaultExpressionSyntax"
        | :? ElementAccessExpressionSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "ElementAccessExpressionSyntax"
        | :? ElementBindingExpressionSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "ElementBindingExpressionSyntax"
        | :? ImplicitArrayCreationExpressionSyntax as x -> 
            printfn "ImplicitArrayCreationExpressionSyntax: %b" (x.Initializer = null)
            let init = CSharpStatementWalker.ParseExpression (x.Initializer)
            let isNakedRef = ref true
            Expr.ArrayOrListOfSeqExpr (true, Expr.CompExpr (true, isNakedRef, init))
            
        | :? ImplicitElementAccessSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "ImplicitElementAccessSyntax"
        | :? ImplicitStackAllocArrayCreationExpressionSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "ImplicitStackAllocArrayCreationExpressionSyntax"
        | :? InitializerExpressionSyntax as x -> 

            x.Expressions
            |> Seq.map (fun x -> printfn "InitializerExpressionSyntax: %b" (x = null); x)
            |> Seq.map (CSharpStatementWalker.ParseExpression >> (fun x -> 
                match x with 
                | Expr.LongIdentSet (LongIdentWithDots ([ident], _), value) -> 
                    Expr.App (ExprAtomicFlag.NonAtomic, false, 
                                Expr.App (ExprAtomicFlag.NonAtomic, true, Expr.Ident "op_Equality", Expr.Ident ident.idText),
                                value) 
                | _ -> x))
            |> Seq.toList
            |> Expr.Tuple

        | :? InstanceExpressionSyntax as x -> toLongIdent "this" //(fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "InstanceExpressionSyntax"
        | :? InterpolatedStringExpressionSyntax as x -> 

            let args = 
                x.Contents 
                |> Seq.map (fun x -> CSharpStatementWalker.ParseInterpolatedStringContentSyntax x) 
                |> Seq.toList

            let stringFormat = 
                args 
                |> List.map (function Expr.Const (SynConst.String (c,_)) -> c | _ -> "%O")
                |> String.concat ""

            let args = args |> List.choose (function Expr.Paren _ as e -> Some e | _ -> None)

            (toLongIdent "sprintf" :: (Expr.Const (SynConst.String (stringFormat, range0))) :: args)
            |> List.reduce (fun a b -> Expr.App (ExprAtomicFlag.NonAtomic, false, a, b) )
        | :? InvocationExpressionSyntax as x -> 
        
            let args =  
                x.ArgumentList.Arguments 
                |> Seq.map (fun x -> CSharpStatementWalker.ParseChsarpNode x )
                |> Seq.toList
                |> Expr.Tuple 

            printfn "InvocationExpressionSyntax: %A" args
            printfn "Expression: %A, %A" x.Expression (x.Expression.GetType())

            let name = x.Expression |> CSharpStatementWalker.ParseChsarpNode
            printfn "Nmae: %A" name
            printfn ""
            Expr.App (ExprAtomicFlag.NonAtomic, false, name, Expr.Paren args)

        //| :? IsPatternExpressionSyntax as x -> ()
        | :? LiteralExpressionSyntax as x -> 
            x.Token |> CSharpStatementWalker.ParseToken
        //| :? MakeRefExpressionSyntax as x -> ()
        | :? MemberAccessExpressionSyntax as x ->

            //x.Name
            match x.OperatorToken.Text with 
            | "." -> Expr.DotGet(x.Expression |> CSharpStatementWalker.ParseExpression, x.Name.WithoutTrivia().ToFullString() |> toLongIdentWithDots)

        | :? MemberBindingExpressionSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "MemberBindingExpressionSyntax"
        | :? ObjectCreationExpressionSyntax as x -> 

            let typeName = x.Type.WithoutTrivia().ToFullString() |> toLongIdentWithDots
            printfn "ObjectCreationExpressionSyntax: %b" (x.Initializer = null)

            let init = 
                match x.Initializer with
                | null ->  Expr.Const SynConst.Unit
                | initExp ->  CSharpStatementWalker.ParseExpression (initExp)

            let joinArgs xs = 
                match xs, init with 
                | Expr.Tuple xs, Expr.Tuple ys -> Expr.Tuple (xs @ ys)
                | Expr.Tuple xs, Expr.Const SynConst.Unit -> Expr.Tuple xs
                | _, _ -> failwithf "Unexpected synax constructing class: %s" <| x.Type.ToFullString()                

            let args = 
                match x.ArgumentList with 
                | null -> Expr.Const SynConst.Unit
                | x -> CSharpStatementWalker.ParseChsarpNode (x, true)

            let args = 
                match args with
                | Expr.Paren xs -> joinArgs xs
                | Expr.Const SynConst.Unit -> joinArgs (Expr.Tuple [])
                | _ -> failwithf "Unexpected synax constructing class: %s" <| x.Type.ToFullString()

            Expr.New (false, typeName |> SynType.LongIdent, Expr.Paren args)
        | :? OmittedArraySizeExpressionSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "OmittedArraySizeExpressionSyntax"
        | :? ParenthesizedExpressionSyntax as x -> CSharpStatementWalker.ParseExpression x.Expression
        | :? PostfixUnaryExpressionSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "PostfixUnaryExpressionSyntax"
        | :? PrefixUnaryExpressionSyntax as x -> 
            match x.Kind() with 
            | SyntaxKind.LogicalNotExpression -> 
                Expr.App(ExprAtomicFlag.NonAtomic, false, PrettyNaming.CompileOpName "not" |> Expr.Ident, x.Operand |> CSharpStatementWalker.ParseExpression)

                //x.ToFullString().Replace("!", "not ") |> Line
            | _ -> sprintf "Unkown C# token: %s" (x.WithoutTrivia().ToFullString()) |> failwith
        | :? QueryExpressionSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "QueryExpressionSyntax"
        | :? RefExpressionSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "RefExpressionSyntax"
        | :? RefTypeExpressionSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "RefTypeExpressionSyntax"
        | :? RefValueExpressionSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "RefValueExpressionSyntax"
        | :? SizeOfExpressionSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "SizeOfExpressionSyntax"
        | :? StackAllocArrayCreationExpressionSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "StackAllocArrayCreationExpressionSyntax"
        | :? ThrowExpressionSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "ThrowExpressionSyntax"
        | :? TupleExpressionSyntax as x -> (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "TupleExpressionSyntax"
        | :? TypeOfExpressionSyntax as x -> 
            let ident = x.Type.WithoutTrivia().ToFullString() |> toLongIdentWithDots |> SynType.LongIdent |> List.singleton
            printfn "%A" ident
            Expr.TypeApp (Expr.Ident "typeof", ident)

        | :? TypeSyntax as x -> 
            printfn "TypeSyntax: %A" x
            printfn "TypeSyntax: %A" <| x.Kind()
            
            (fun () -> x.WithoutTrivia().ToFullString() |> toLongIdent) |> debugFormat "TypeSyntax"

    static member ParseNodeOrToken(node:SyntaxNodeOrToken): Expr = 

        if node.IsToken then 
            node.AsToken() |> CSharpStatementWalker.ParseToken
        else 
            node.AsNode() 
            |> CSharpStatementWalker.ToCsharpSyntaxNode
            |> Option.map (fun x -> CSharpStatementWalker.ParseChsarpNode x)
            |> function 
                | Some x -> x
                | None -> failwith "VB is not supported"
    


type FSharperTreeBuilder() = 
    inherit CSharpSyntaxWalker(SyntaxWalkerDepth.Token)
    
    member this.VisitNamespaceDeclaration (node:NamespaceDeclarationSyntax ) = 

        let classes = 
            node.ChildNodes().OfType<ClassDeclarationSyntax>()
            |> Seq.map this.VisitClassDeclaration
            |> Seq.toList

        let interfaces = 
            node.ChildNodes().OfType<InterfaceDeclarationSyntax>()
            |> Seq.map this.VisitInterfaceDeclaration
            |> Seq.toList
            
        {
            Namespace.Name = node.Name.WithoutTrivia().ToFullString()
            Namespace.Classes = classes
            Namespace.Interfaces = interfaces
        } 


    member this.VisitInterfaceDeclaration(node:InterfaceDeclarationSyntax) =    

        let members = 
            node.Members |> Seq.map (fun x -> x.ToFullString()) |> Seq.toList

        {
            Interface.Name = node.Identifier.WithoutTrivia().Text
            Methods = members
        }

    member this.VisitClassDeclaration(node:ClassDeclarationSyntax ) =

        let attrs = 
            node.AttributeLists
            |> Seq.collect (fun x -> 
                x.Attributes 
                |> Seq.map (fun x -> 
                    let attributesValues = 
                        x.ArgumentList
                        |> Option.ofObj
                        |> Option.map (fun x -> x.Arguments)
                        |> (Option.toList >> List.toSeq >> Seq.concat)
                        |> Seq.map (fun y -> 

                            if isNull y.NameEquals then 
                                CSharpStatementWalker.ParseChsarpNode (y.Expression, false) |>  AttributeValue
                            else 
                                NamedAttributeValue 
                                    (CSharpStatementWalker.ParseChsarpNode (y.NameEquals.Name, false), 
                                    CSharpStatementWalker.ParseChsarpNode (y.Expression, false)) )
                        |> Seq.toList
                        
                    {
                        Attribute.Name = x.Name.WithoutTrivia().ToFullString()
                        Attribute.Parameters = attributesValues }
            ))
            |> Seq.toList

        let typeParameters = 
            node.TypeParameterList 
            |> Option.ofObj 
            |> Option.map (fun x -> 
                x.Parameters 
                |> Seq.map (fun x -> x.Identifier.WithoutTrivia().Text) 
                |> Seq.toList )
            |> Option.toList
            |> List.concat

        let rec doesBaseTypeBeginWithI = function
        | SynType.App  (x, _, _, _, _, _, _) -> doesBaseTypeBeginWithI x
        | SynType.LongIdent x -> 
            match ParserUtil.joinLongIdentWithDots x with 
            | x when x.StartsWith "I" -> true
            | _ -> false
        | _ -> false

        let baseTypes = 
            node.BaseList 
            |> Option.ofObj 
            |> Option.bind (fun x -> 

                x.Types 
                |> Seq.map (fun x -> ParserUtil.parseType x.Type)
                |> Seq.toList
                |> function 
                | [] -> None
                | x::_ as xs when doesBaseTypeBeginWithI x -> Some (None, xs) 
                | x::xs -> Some (Some x, xs)  )

        let ctors = 
            node.ChildNodes().OfType<ConstructorDeclarationSyntax>()
            |> Seq.map this.VisitConstructorDeclaration
            |> Seq.toList

        let fields = 
            node.ChildNodes().OfType<FieldDeclarationSyntax>()
            |> Seq.map this.VisitFieldDeclaration
            |> Seq.concat
            |> Seq.toList

        let methods = 
            node.ChildNodes().OfType<MethodDeclarationSyntax>()
            |> Seq.map this.VisitMethodDeclaration
            |> Seq.toList

        let properties = 
            node.ChildNodes().OfType<PropertyDeclarationSyntax>()
            |> Seq.map this.VisitPropertyDeclaration
            |> Seq.toList
            
        {
            Name = { ClassName.Name = node.Identifier.ValueText; Generics = [] }
            ImplementInterfaces = baseTypes |> Option.map (snd) |> Option.toList |> List.concat
            BaseClass = baseTypes |> Option.bind fst
            Constructors = ctors
            Fields = fields
            Methods = methods
            Properties = properties
            TypeParameters = typeParameters
            Attributes = attrs  }


    member this.VisitConstructorDeclaration (node:ConstructorDeclarationSyntax) = 
        {
            Ctor.Body = Line "" // TODO
                //node.Body.Statements 
                //|> Seq.map (fun x -> x.WithoutTrailingTrivia().WithoutLeadingTrivia().ToFullString().Replace(";", ""))
                //|> Seq.map Line
                //|> Seq.toList

            Ctor.Parameters = 
                node.ParameterList.Parameters 
                |> Seq.map (fun x -> 
                    {Parameter.Name = x.Identifier.WithoutTrivia().Text; Type = ParserUtil.parseType x.Type })
                |> Seq.toList

            SubclassArgs = 
                node.Initializer |> Option.ofObj 
                |> Option.bind (fun x -> Option.ofObj x.ArgumentList)
                |> Option.map (fun x -> x.Arguments)
                |> Option.map (fun x -> 
                    x |> Seq.map (fun x -> x.WithoutTrivia().ToFullString()) |> Seq.toList)
                |> Option.toList
                |> List.concat
        }

    member this.VisitMethodDeclaration(node:MethodDeclarationSyntax ) = 
        let isPrivate = 
            node.Modifiers |> Seq.exists (fun x -> x.Kind() = SyntaxKind.PrivateKeyword)

        {
            Method.Name = node.Identifier.WithoutTrivia().Text
            Method.IsVirtual = node.Modifiers |> Seq.exists (fun x -> x.Kind() = SyntaxKind.VirtualKeyword )
            Method.IsAsync = node.Modifiers |> Seq.exists (fun x -> x.Kind() = SyntaxKind.AsyncKeyword )
            Method.IsPrivate = isPrivate
            Method.IsOverride = node.Modifiers |> Seq.exists (fun x -> x.Kind() = SyntaxKind.OverrideKeyword )

            Method.ReturnType = node.ReturnType.WithoutTrivia().ToFullString()
            Method.Parameters = 
                node.ParameterList.Parameters 
                |> Seq.map (fun x -> 
                    {Parameter.Name = x.Identifier.WithoutTrivia().Text; Type = ParserUtil.parseType x.Type })
                |> Seq.toList
            Method.Body =
                node.Body |> Option.ofObj |> Option.map (fun x -> 
                    x.Statements 
                    |> Seq.map CSharpStatementWalker.ParseChsarpNode
                    |> sequential  )
                |> function 
                | Some x -> x
                | None -> Expr.Const SynConst.Unit

            Method.Accessibility = if isPrivate then Some SynAccess.Private else None
            Method.Attributes = 
                node.AttributeLists 
                |> Seq.map (fun x -> x.Attributes |> Seq.map (fun x -> 
                    let name = x.Name.WithoutTrivia().ToFullString() |> toLongIdentWithDots
                    let args = 
                        match x.ArgumentList with 
                        | null -> None
                        | x -> CSharpStatementWalker.ParseChsarpNode (x, true) |> Some 

                    name, args ))
                |> Seq.concat
                |> Seq.toList
        }

    member this.VisitFieldDeclaration (node:FieldDeclarationSyntax): Field seq = 

        let isPublic = node.Modifiers |> Seq.exists (fun x -> x.Kind() = SyntaxKind.PublicKeyword)
        let isConstant = node.Modifiers |> Seq.exists (fun x -> x.Kind() = SyntaxKind.ConstKeyword)

        node.Declaration.Variables
        |> Seq.map (fun x -> 
            {
                Field.Name =  x.Identifier.WithoutTrivia().ToFullString()
                Field.Type = node.Declaration.Type.WithoutTrivia().ToFullString()
                Field.IsPublic = isPublic
                Field.Initilizer = x.Initializer |> Option.ofObj |> Option.map (fun x -> x.Value |> CSharpStatementWalker.ParseExpression )
                Field.IsConst = isConstant
            })

    member this.VisitPropertyDeclaration (node:PropertyDeclarationSyntax) = 

        let parseAccessorDeclaration (node:AccessorDeclarationSyntax) = 

            let expression = node.ExpressionBody |> Option.ofObj
            let statement = node.Body |> Option.ofObj 

            match expression, statement with 
            | None, None -> []
            | Some e, None -> [CSharpStatementWalker.ParseChsarpNode e]
            | None, Some s -> s.Statements |> Seq.map CSharpStatementWalker.ParseChsarpNode |> Seq.toList
            | Some e, Some s -> [CSharpStatementWalker.ParseChsarpNode e;] @ (s.Statements |> Seq.map CSharpStatementWalker.ParseChsarpNode |> Seq.toList)

        let processAccessorForAccessorType accessor = 
            node.AccessorList.Accessors 
            |> Seq.filter (fun x -> x.Kind() = accessor)
            |> Seq.map parseAccessorDeclaration
            |> Seq.toList
            |> List.concat
            |> function
            | _::_::_ as xs -> xs |> Seq.reduce (fun a b -> Expr.Sequential (SequencePointsAtSeq, true, a,b)) |> Some
            | x::[] -> x |> Some
            | _ -> None

        let getStatements = processAccessorForAccessorType SyntaxKind.GetAccessorDeclaration
        let setStatements =  processAccessorForAccessorType SyntaxKind.SetAccessorDeclaration

        let access = 
            match node.Modifiers |> Seq.map (fun x -> x.WithoutTrivia().ToFullString()) |> Seq.toList with 
            | ["internal"] -> Some SynAccess.Internal
            | ["private"] -> Some SynAccess.Private
            | _  -> None
            
        {
            Prop.Name = node.Identifier.WithoutTrivia().Text
            Type = node.Type.WithoutTrivia().ToFullString()
            Prop.Get = getStatements 
            Prop.Set = setStatements
            Access = access
        }

    member this.VisitUsingDirective (node:UsingDirectiveSyntax ) = 
        node.ChildNodes() |> ignore

        { Namespace = node.Name.WithoutTrivia().ToFullString() }


    member this.ParseSyntax tree (x: SyntaxNode) = 

        let result = 
            match x with
            | :? UsingDirectiveSyntax as x -> x |> this.VisitUsingDirective |> UsingStatement
            | :? NamespaceDeclarationSyntax as x -> x |> this.VisitNamespaceDeclaration |> Namespace
            | :? MethodDeclarationSyntax as x -> x |> this.VisitMethodDeclaration |> Method
            | :? InterfaceDeclarationSyntax as x -> x |> this.VisitInterfaceDeclaration |> Interface
            | :? ClassDeclarationSyntax as x -> x |> this.VisitClassDeclaration |> Class
            | :? FieldDeclarationSyntax as x -> x |> this.VisitFieldDeclaration |> FSharper.Core.Field
            | :? PropertyDeclarationSyntax as x -> x |> this.VisitPropertyDeclaration |> Prop
            | x -> printfn "Skipping element: %A" <| x.Kind(); Empty

        match tree with 
        | None -> result |> Some
        | Some tree -> 
            match tree, result with 
            | Empty, x -> x |> Some
            | File f, File _ -> failwith "Meging files, not implemented"
            | File f, Interface i -> failwith "Meging interface with file, not implemented"
            | File f, Class c -> failwith "Meging class with file, not implemented"
            | File f, FSharper.Core.Field field -> failwith "Meging files, not implemented"
            | File f, Prop p -> failwith "Meging files, not implemented"
            | File f, Method m -> failwith "Meging files, not implemented"
            | File f, Namespace ns -> 
                {f with Namespaces = ns :: f.Namespaces} |> File |> Some
            | File f, UsingStatement m -> { f with UsingStatements = m :: f.UsingStatements } |> File |> Some
            | UsingStatement i, UsingStatement i2 -> 
                {
                    UsingStatements = [i;i2]
                    Namespaces = []
                } |> File |> Some
            | UsingStatement i, Namespace ns -> 
                {
                    UsingStatements = [i]
                    Namespaces = [ns]
                } |> File |> Some
            | FSharper.Core.Field f, FSharper.Core.Field fPrime -> 
                seq {
                    yield! f
                    yield! fPrime
                } |> FSharper.Core.Field |> Some
            | _, _ -> sprintf "C# not supported: %A, %A" tree result |> failwith