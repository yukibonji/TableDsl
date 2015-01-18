﻿namespace TableDsl.Parser

open Basis.Core
open FParsec
open TableDsl
open TableDsl.Parser
open TableDsl.Parser.Types
open TableDsl.Parser.Primitives

module internal Impl =
  let pNonQuotedTypeParamElem =
    let rec pNonQuotedTypeParamElem terminators : Parser<string> =
      let p = parse {
        let! ch = anyChar
        return!
          match ch with
          | '(' -> parse {
                     let! result = pNonQuotedTypeParamElem [')']
                     do! pchar ')' |>> ignore
                     return "(" + result + ")" }
          | x when List.exists ((=)x) terminators -> pzero
          | x -> preturn (string x)
      }
      many (attempt p) |>> (fun xs -> System.String.Concat(xs))
    pNonQuotedTypeParamElem [','; ')']

  let pOpenTypeRefWithoutAttributes, pOpenTypeRefWithoutAttributesRef = createParserForwardedToRef ()

  let pNullableParam = parse {
    do! pSkipOnelineToken "("
    let! typ, _ = pOpenTypeRefWithoutAttributes
    do! pSkipOnelineToken ")"
    return [BoundType typ]
  }

  let pOpenTypeParamElem =
    pTypeVariableName |>> TypeVariable <|> (pSqlValue |>> BoundValue) // ここはpSqlValueじゃなくて、pSqlTypeか？

  let pOpenTypeParam typeName =
    if typeName = "nullable" then
      pNullableParam
    else
      sepBy pOpenTypeParamElem (pSkipToken ",") |> between (pchar '(') (pchar ')')

  let expandAttr' replacingMap = function
  | Var v ->
      match Map.tryFind v replacingMap with
      | Some (BoundValue v) -> Lit v
      | _ -> Var v
  | Lit l -> Lit l

  let expandAttr replacingMap = function
  | ComplexColAttr (key, values) -> ComplexColAttr (key, values |> List.map (expandAttr' replacingMap))
  | other -> other

  let replaceTypeParams'' replacingMap = List.map (function TypeVariable key -> replacingMap |> Map.find key | other -> other)

  let rec replaceTypeParams' replacingMap = function
  | BuiltinType ty -> BuiltinType { ty with TypeParameters = ty.TypeParameters |> replaceTypeParams'' replacingMap }
  | AliasDef (ty, originalType) ->
      let replaced = ty.TypeParameters |> replaceTypeParams'' replacingMap
      AliasDef ({ ty with TypeParameters = replaced }, replaceTypeParams replacingMap originalType)
  | EnumTypeDef ty -> EnumTypeDef ty

  and replaceTypeParams replacingMap colTypeDef =
    { colTypeDef with ColumnTypeDef = replaceTypeParams' replacingMap colTypeDef.ColumnTypeDef
                      ColumnTypeDefAttributes = colTypeDef.ColumnTypeDefAttributes |> List.map (expandAttr replacingMap) }

  let resolveType typeName typeParams env =
    let resolved =
      env |> List.tryFind (fun (name, paramCount, _, _, _, _) -> name = typeName && paramCount = (List.length typeParams)) 
    match resolved with
    | Some (_name, _paramCount, t, attrs, colSummary, colJpName) ->
      let typeDef, attrs =
        match t with
        | BuiltinType ty -> (BuiltinType { ty with TypeParameters = typeParams }), attrs
        | AliasDef (ty, originalType) ->
            let replacingMap =
              Map.ofList (
                (ty.TypeParameters, typeParams)
                ||> List.zip
                |> List.choose (function (TypeVariable key, value) -> Some (key, value) | _ -> None))
            let attrs = attrs |> List.map (expandAttr replacingMap)
            (AliasDef ({ ty with TypeParameters = typeParams }, replaceTypeParams replacingMap originalType)), attrs
        | EnumTypeDef ty -> (EnumTypeDef ty), attrs
      preturn { ColumnTypeDefSummary = colSummary; ColumnTypeDef = typeDef; ColumnTypeDefJpName = colJpName; ColumnTypeDefAttributes = attrs }
    | None -> failFatally (sprintf "%sという型が見つかりませんでした。" typeName)

  let pClosedTypeRefWithoutAttributes = parse {
    let! typeName = pName
    let! typeParams = opt (attempt (pOpenTypeParam typeName))
    let typeParams =
      match typeParams with
      | None -> []
      | Some x -> x
    let! env = getUserState
    if typeName = "nullable" then
      match typeParams with
      | [BoundType t] -> return (t, true)
      | other -> return! failFatally (sprintf "nullableには一つしか型パラメータを指定できません。: %A" other)
    else
      let! typ = resolveType typeName typeParams env
      return (typ, false)
  }

  pOpenTypeRefWithoutAttributesRef := parse {
    let! (closedTypeRef, nullable) = pClosedTypeRefWithoutAttributes
    if nullable then
      return! failFatally "nullableを元に型を定義することは出来ません。"
    else
      return (closedTypeRef, nullable)
  }

  let pAttributeValue =
    (attempt (pSqlValue .>> followedBy (pchar ';' <|> pchar ' ' <|> newline)))
    <|> (attempt pIndexSetting)
    <|> pPlaceholders

  let pClosedComplexAttribute = parse {
    do! wsnl |>> ignore
    let! name = pName
    do! pSkipToken "="
    let! value = pAttributeValue
    return ComplexColAttr (name, [ Lit value ])
  }
  let pVarAttrValueElem = pTypeVariableName |>> Var
  let pLitAttrValueElem = pAttributeValue |>> Lit
  let pAttributeValueElem = pVarAttrValueElem <|> pLitAttrValueElem
  let pOpenComplexAttribute = parse {
    do! wsnl |>> ignore
    let! name = pName
    do! pSkipToken "="
    let! values = many1 pAttributeValueElem
    return ComplexColAttr (name, values)
  }
  let pSimpleAttribute = wsnl >>. pName |>> (fun name -> SimpleColAttr name)
  let pClosedAttribute = attempt pClosedComplexAttribute <|> pSimpleAttribute
  let pOpenAttribute = attempt pOpenComplexAttribute <|> pSimpleAttribute
  let pClosedAttributes = sepBy1 pClosedAttribute (pchar ';') // 改行もセパレータ扱いにする？
  let pOpenAttributes = sepBy1 pOpenAttribute (pchar ';') // 改行もセパレータ扱いにする？

  let pClosedTypeRefWithAttributes = parse {
    do! pSkipOnelineToken "{"
    let! typ, nullable = pClosedTypeRefWithoutAttributes
    do! pSkipToken "with"
    let! attrs = pClosedAttributes
    do! pSkipOnelineToken "}"
    return (typ, nullable, attrs)
  }

  let pOpenTypeRefWithAttributes = parse {
    do! pSkipOnelineToken "{"
    let! typ, nullable = pOpenTypeRefWithoutAttributes
    if nullable then
      return! failFatally "nullableを元に型を定義することは出来ません。"
    else
      do! pSkipToken "with"
      let! attrs = pOpenAttributes
      do! pSkipOnelineToken "}"
      return (typ, nullable, attrs)
  }

  let pOpenTypeRef = pOpenTypeRefWithAttributes <|> (pOpenTypeRefWithoutAttributes |>> fun (t, n) -> (t, n, []))
  let pClosedTypeRef = parse {
    let! typeDef, nullable, attrs =
      pClosedTypeRefWithAttributes <|> (pClosedTypeRefWithoutAttributes |>> fun (t, n) -> (t, n, []))
    let attrs =
      attrs
      |> List.map (function
                   | SimpleColAttr k -> SimpleAttr k
                   | ComplexColAttr (k, v) -> ComplexAttr (k, [v |> List.map (function Lit l -> l) |> String.concat ""]))
    return {
      Type = TypeInfo.FromColumnTypeDef(typeDef)
      IsNullable = nullable
      ColumnTypeRefName = typeDef.TypeName
      ColumnTypeRefParams = typeDef.Params |> List.map (function BoundValue v -> v)
      ColumnTypeRefAttributes = attrs
    }
  }

  let pAliasDef name typeParams = parse {
    let! body, _nullable, attrs = pOpenTypeRef
    return (AliasDef ({ TypeName = name; TypeParameters = typeParams }, body), attrs)
  }

  let pEnumCase = attempt (pSkipOnelineToken "|") >>. pName .>>. pJpNameOpt .>> pSkipOnelineToken "=" .>>. pInteger |>> fun ((n, j), v) -> (n, j, v)
  let pEnumCases = sepEndBy1 pEnumCase newline
  let pEnumTypeDef name _typeParams = parse {
    let! cases = pEnumCases
    do! pSkipToken "based"
    let! based, _nullable, attrs = pOpenTypeRef
    match based.ColumnTypeDef with
    | BuiltinType typ -> return (EnumTypeDef { EnumTypeName = name; BaseType = typ; Cases = cases }), attrs
    | AliasDef (typ, _orig) -> return! failFatally (sprintf "列挙型の定義(%s)の基底型には組み込み型しか指定できませんが、列定義(%s)が指定されました。" name typ.TypeName)
    | EnumTypeDef typ -> return! failFatally (sprintf "列挙型の定義(%s)の基底型には組み込み型しか指定できませんが、他の列挙型(%s)が指定されました。" name typ.EnumTypeName)
  }

  let pTypeDef name typeParams = pEnumTypeDef name typeParams <|> pAliasDef name typeParams

  let pTypeParams =
    sepBy pTypeVariableName (pSkipToken ",")
    |> between (pSkipToken "(") (pSkipToken ")")

  let checkColTypeParams name xs =
    let rec tryFindDup existsParams = function
    | x::_ when List.exists ((=)x) existsParams -> Some x
    | x::xs -> tryFindDup (x::existsParams) xs
    | [] -> None

    option {
      let! xs = xs
      let! dup = tryFindDup [] xs
      return failFatally (sprintf "型%sの定義で型変数%sが重複しています。" name dup)
    }

  let pColTypeDef = parse {
    let! colTypeSummary = pSummaryOpt
    do! pSkipToken "coltype"
    let! colTypeName = pColTypeName
    let! colTypeParams = pTypeParams |> attempt |> opt
    do! match checkColTypeParams colTypeName colTypeParams with Some p -> p | None -> preturn ()
    let colTypeParams =
      colTypeParams
      |> function Some x -> x | _ -> []
      |> List.map (fun p -> TypeVariable p)
    let! colTypeJpName = pJpNameOpt
    do! pSkipToken "="
    let! typ, attrs = pTypeDef colTypeName colTypeParams
    do! updateUserState (fun state -> (colTypeName, colTypeParams.Length, typ, attrs, colTypeSummary, colTypeJpName)::state)
    return ColTypeDef { ColumnTypeDefSummary = colTypeSummary; ColumnTypeDef = typ; ColumnTypeDefJpName = colTypeJpName; ColumnTypeDefAttributes = attrs }
  }

  let pColumnDef = parse {
    let! colSummary = pSummaryOpt
    do! wsnl |>> ignore
    let! colName = pColName
    let! colJpName =
      if colName = "_" then preturn None else pJpNameOpt
    do! pSkipToken ":"
    let! colType = pClosedTypeRef
    return { ColumnSummary = colSummary
             ColumnName = if colName = "_" then Wildcard else ColumnName (colName, colJpName)
             ColumnType = colType }
  }

  let pTableComplexAttr = parse {
    let! attrName = pName
    do! pSkipOnelineToken "("
    let! attrValues = sepBy pSqlValue (pchar ',' .>> wsnl)
    do! pSkipOnelineToken ")"
    return ComplexAttr (attrName, attrValues)
  }

  let pTableSimpleAttr = parse {
    let! attrName = pName
    return SimpleAttr attrName
  }

  let pTableAttribute = parse {
    do! wsnl |>> ignore
    do! pSkipOnelineToken "[<"
    let! attr = (attempt pTableComplexAttr <|> pTableSimpleAttr)
    do! pSkipOnelineToken ">]"
    return attr
  }

  let pTableDef = parse {
    let! tableSummary = pSummaryOpt
    let! tableAttributes = many (attempt pTableAttribute)
    do! pSkipToken "table"
    let! tableName = pTableName
    let! tableJpName = pJpNameOpt
    do! pSkipToken "="
    let! colDefs =
      sepEndBy (attempt pColumnDef) newline
      |> between (pSkipOnelineToken "{") (pSkipOnelineToken "}")
    return TableDef { TableSummary = tableSummary
                      TableAttributes = tableAttributes
                      TableName = tableName
                      TableJpName = tableJpName
                      ColumnDefs = colDefs }
  }

  // TODO : 一通り完成したら、エラーメッセージを入れる？(例: 「トップレベルの要素はcoltypeかtableのみが許されています。」)
  let parser = (sepBy ((attempt pColTypeDef) <|> (attempt pTableDef)) newline) .>> eof