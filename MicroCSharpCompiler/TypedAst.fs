﻿module TypedAst

open Ast
open System
open System.Reflection
open System.Reflection.Emit

type TFile = 
| TFile of string list * TNamespaceBody list
and TNamespaceBody = 
| TInterface of TypeBuilder * TInterfaceBody list
| TClass of TypeBuilder * TClassBody list
| TStruct of TypeBuilder
| TEnum of Type 
and TInterfaceBody =
| TMethod of Type option * Name * (Type * Name) list
and TClassBody = 
| TMethod of AccessModifier * Type option * Name * (Type * Name) list * Expr list 
and TExpr = 
| TVar of Type * Name * TExpr option
//Are the following needed?
| TRef of Name
| TString of string
| TInt of int
| TFloat of float32
| TDouble of float
| TBool of bool
//End
| TCall of Name * TExpr list
| TAdd of TExpr * TExpr

let accessModifierToTypeAttribute = function
| Public -> TypeAttributes.Public
| Private -> failwith "Protected is not valid on Types"
| Internal -> TypeAttributes.NotPublic
| Protected -> failwith "Protected is not valid on Types"

let accessModifierToMethodAttribute = function
| Public -> MethodAttributes.Public
| Private -> MethodAttributes.Private
| Internal -> MethodAttributes.Assembly
| Protected -> MethodAttributes.Family

//TODO - Lookup locally defined types
let getTypeByName name usings = 
    let t = Type.GetType name
    match t with
    | null -> 
        let usings = usings |>List.rev //We reverse the order as the last defined takes greatest precedence
        let possibleTypeNames = usings |> List.map(fun using -> using + "." + name)
        let loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies()

        let possibleFullyQualifiedTypes = 
            possibleTypeNames 
            |> List.collect (fun t -> loadedAssemblies |> Seq.map(fun a -> t + "," + a.FullName) |> Seq.toList)
            |> List.map(fun tn -> Type.GetType(tn))
            |> List.filter(fun t -> t <> null)
        match possibleFullyQualifiedTypes with
        | head::_ -> head
        | _ -> null
        
    | t -> t
    
let resolveType name usings = 
    match name with
    | "void" -> None
    | "int" -> Some(typeof<int>)
    | "float" -> Some(typeof<float32>)
    | "double" -> Some(typeof<float>)
    | "string" -> Some(typeof<string>)
    | "bool" -> Some(typeof<bool>)
    | "object" -> Some(typeof<obj>)
    | typeName -> 
        let t = getTypeByName typeName usings
        if t <> null then Some(t) 
        else failwith "Unrecognized type"


let (|CLASSMETHOD|_|) (b, usings) = 
    match b with
    | ClassBody.Method(acccessModifier, typename, name, parameters, exprList) -> 
        let returnType = resolveType typename usings
        let parameters = parameters |> List.map(fun (Parameter(typename, name)) -> (resolveType typename usings).Value, name) //todo - dont use .Value
        Some(TClassBody.TMethod(acccessModifier, returnType, name, parameters, exprList))
    | _ -> None

let (|CLASS|_|) (mb:ModuleBuilder, body:NamespaceBody, namespaceName, usings) = 
    match body with
    | Class(name, visibility, body) -> 
        let definedType = mb.DefineType(namespaceName+"."+name, accessModifierToTypeAttribute visibility)
        let methods = body |> List.choose(fun b -> match (b, usings) with CLASSMETHOD(m) -> Some(m) | _ -> None)
        Some(definedType, methods)
    | _ -> None

let (|INTERFACEMETHOD|_|) (b, usings) =
    match b with 
    | Method(typename, name, parameters) -> 
        let returnType = resolveType typename usings
        let parameters = parameters|>List.map(fun (Parameter(typename, name)) -> (resolveType typename usings).Value, name) //todo - dont use .Value
        Some(TInterfaceBody.TMethod(returnType, name, parameters))
    | _ -> None

let (|INTERFACE|_|) (mb:ModuleBuilder, body:NamespaceBody, namespaceName, usings) = 
    match body with
    | Interface(name, visibility, body) -> 
        let definedType = mb.DefineType(namespaceName+"."+name, TypeAttributes.Abstract ||| TypeAttributes.Interface ||| accessModifierToTypeAttribute visibility)
        let methods = body |> List.choose(fun b -> match (b, usings) with INTERFACEMETHOD(m) -> Some(m) | _ -> None)
        Some(definedType, methods)
    | _ -> None

//Rule break here - I just compile the Enums here - no point doing a 2 pass I think - maybe I'll change my mind later
let (|ENUM|_|) (mb:ModuleBuilder, body:NamespaceBody, namespaceName, usings) = 
    match body with
    | Enum(name, visibility, values) -> 
        let eb = mb.DefineEnum(namespaceName+"."+name, accessModifierToTypeAttribute visibility, typeof<int>)
        values|>List.mapi(fun i n -> eb.DefineLiteral(n,i))|>ignore
        Some(eb.CreateType())
    | _ -> None

let compileType mb namespaceBody namespaceName usings =
    match (mb, namespaceBody, namespaceName, usings) with
    | CLASS(tb) -> Some(TClass(tb))
    | INTERFACE(tb, body) -> Some(TInterface(tb, body)) 
    | ENUM(tb) -> Some(TEnum(tb))
    | _ -> failwith "Unrecognized Namespace Body"

let preCompile (ast: File) filename = 
    let aName = AssemblyName(filename)
    let ab = AppDomain.CurrentDomain.DefineDynamicAssembly(aName, AssemblyBuilderAccess.RunAndSave)
    let mb = ab.DefineDynamicModule(aName.Name, aName.Name + ".dll", true) //true means debug it seems
    match ast with
    | File fileBody -> 
        let usings = fileBody |> List.choose(fun x -> match x with Using(using) -> Some(using) | _ -> None)
        let namespaces = fileBody |> List.choose(fun x -> match x with Namespace(name, bodyList) -> Some(name, bodyList) | _ -> None)
        ab, TFile(usings, 
                    namespaces
                    |>List.collect(fun (name, body) -> body|>List.choose(fun b -> compileType mb b name usings)))
        