/*
Copyright 2017 YANG Huan (sy.yanghuan@gmail.com).

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

  http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis;
using CSharpLua.LuaAst;

namespace CSharpLua {
  public sealed partial class LuaSyntaxNodeTransform {
    private Stack<LuaIdentifierNameSyntax> conditionalTemps_ = new Stack<LuaIdentifierNameSyntax>();

    public override LuaSyntaxNode VisitObjectCreationExpression(ObjectCreationExpressionSyntax node) {
      var constExpression = GetConstExpression(node);
      if (constExpression != null) {
        return constExpression;
      }

      var symbol = (IMethodSymbol)semanticModel_.GetSymbolInfo(node).Symbol;
      LuaExpressionSyntax creationExpression;
      if (symbol != null) {
        string codeTemplate = XmlMetaProvider.GetMethodCodeTemplate(symbol);
        if (codeTemplate != null) {
          creationExpression = BuildCodeTemplateExpression(codeTemplate, null, node.ArgumentList.Arguments.Select(i => i.Expression), symbol.TypeArguments);
        } else if (node.Type.IsKind(SyntaxKind.NullableType)) {
          Contract.Assert(node.ArgumentList.Arguments.Count == 1);
          var argument = node.ArgumentList.Arguments.First();
          return argument.Expression.Accept(this);
        } else if (symbol.ContainingType.IsTupleType) {
          var expressions = node.ArgumentList.Arguments.Select(i => (LuaExpressionSyntax)i.Expression.Accept(this));
          return BuildValueTupleCreateExpression(expressions);
        } else {
          var expression = (LuaExpressionSyntax)node.Type.Accept(this);
          var invokeExpression = BuildObjectCreationInvocation(symbol, expression);
          if (node.ArgumentList != null) {
            var arguments = BuildArgumentList(symbol, symbol.Parameters, node.ArgumentList);
            TryRemoveNilArgumentsAtTail(symbol, arguments);
            invokeExpression.AddArguments(arguments);
          }
          creationExpression = invokeExpression;
        }
      } else {
        var type = semanticModel_.GetSymbolInfo(node.Type).Symbol;
        if (type != null && type.Kind == SymbolKind.NamedType) {
          var nameType = (INamedTypeSymbol)type;
          if (nameType.IsDelegateType()) {
            Contract.Assert(node.ArgumentList.Arguments.Count == 1);
            var argument = node.ArgumentList.Arguments.First();
            return argument.Expression.Accept(this);
          }
        }

        Contract.Assert(!node.ArgumentList.Arguments.Any());
        var expression = (LuaExpressionSyntax)node.Type.Accept(this);
        var invokeExpression = new LuaInvocationExpressionSyntax(LuaIdentifierNameSyntax.SystemNew, expression);
        creationExpression = invokeExpression;
      }

      if (node.Initializer == null) {
        return creationExpression;
      } else {
        var functionExpression = BuildObjectInitializerExpression(node.Initializer);
        return new LuaInvocationExpressionSyntax(LuaIdentifierNameSyntax.Create, creationExpression, functionExpression);
      }
    }

    private LuaFunctionExpressionSyntax BuildObjectInitializerExpression(InitializerExpressionSyntax node) {
      LuaFunctionExpressionSyntax function = new LuaFunctionExpressionSyntax();
      PushFunction(function);
      var temp = GetTempIdentifier(node);
      function.AddParameter(temp);
      foreach (var expression in node.Expressions) {
        if (expression.IsKind(SyntaxKind.SimpleAssignmentExpression)) {
          AssignmentExpressionSyntax assignment = (AssignmentExpressionSyntax)expression;
          var left = assignment.Left.Accept(this);
          var right = (LuaExpressionSyntax)assignment.Right.Accept(this);
          if (assignment.Left.IsKind(SyntaxKind.ImplicitElementAccess)) {
            var argumentList = (LuaArgumentListSyntax)left;
            LuaIdentifierNameSyntax methodName = new LuaIdentifierNameSyntax(LuaSyntaxNode.Tokens.Set);
            LuaMemberAccessExpressionSyntax memberAccess = new LuaMemberAccessExpressionSyntax(temp, methodName, true);
            LuaInvocationExpressionSyntax invocation = new LuaInvocationExpressionSyntax(memberAccess);
            invocation.ArgumentList.Arguments.AddRange(argumentList.Arguments);
            invocation.AddArgument(right);
            function.AddStatement(invocation);
          } else {
            var memberAccess = BuildFieldOrPropertyMemberAccessExpression(temp, (LuaExpressionSyntax)left, false);
            var assignmentExpression = BuildLuaSimpleAssignmentExpression(memberAccess, right);
            function.AddStatement(assignmentExpression);
          }
        } else {
          LuaMemberAccessExpressionSyntax memberAccess = new LuaMemberAccessExpressionSyntax(temp, LuaIdentifierNameSyntax.Add, true);
          LuaInvocationExpressionSyntax invocation = new LuaInvocationExpressionSyntax(memberAccess);
          if (expression.IsKind(SyntaxKind.ComplexElementInitializerExpression)) {
            var initializer = (InitializerExpressionSyntax)expression;
            foreach (var expressionNode in initializer.Expressions) {
              var argumnet = (LuaExpressionSyntax)expressionNode.Accept(this);
              invocation.AddArgument(argumnet);
            }
          } else {
            var value = (LuaExpressionSyntax)expression.Accept(this);
            invocation.AddArgument(value);
          }
          function.AddStatement(invocation);
        }
      }

      PopFunction();
      return function;
    }

    public override LuaSyntaxNode VisitAnonymousObjectMemberDeclarator(AnonymousObjectMemberDeclaratorSyntax node) {
      LuaIdentifierNameSyntax name;
      var expression = (LuaExpressionSyntax)node.Expression.Accept(this);
      if (node.NameEquals != null) {
        name = (LuaIdentifierNameSyntax)node.NameEquals.Accept(this);
      } else {
        name = (LuaIdentifierNameSyntax)expression;
      }
      return new LuaKeyValueTableItemSyntax(new LuaTableLiteralKeySyntax(name), expression);
    }

    public override LuaSyntaxNode VisitAnonymousObjectCreationExpression(AnonymousObjectCreationExpressionSyntax node) {
      LuaTableInitializerExpression table = new LuaTableInitializerExpression();
      foreach (var initializer in node.Initializers) {
        var item = (LuaKeyValueTableItemSyntax)initializer.Accept(this);
        table.Items.Add(item);
      }
      return new LuaInvocationExpressionSyntax(LuaIdentifierNameSyntax.AnonymousTypeCreate, table);
    }

    public override LuaSyntaxNode VisitInitializerExpression(InitializerExpressionSyntax node) {
      Contract.Assert(node.IsKind(SyntaxKind.ArrayInitializerExpression));
      var arrayType = BuildArrayTypeFromInitializer(node);
      return BuildArrayCreationExpression(arrayType, node);
    }

    public override LuaSyntaxNode VisitBracketedArgumentList(BracketedArgumentListSyntax node) {
      return BuildArgumentList(node.Arguments);
    }

    public override LuaSyntaxNode VisitImplicitElementAccess(ImplicitElementAccessSyntax node) {
      return node.ArgumentList.Accept(this);
    }

    public override LuaSyntaxNode VisitGenericName(GenericNameSyntax node) {
      ISymbol symbol = semanticModel_.GetSymbolInfo(node).Symbol;
      if (symbol.Kind == SymbolKind.Method) {
        return GetMethodNameExpression((IMethodSymbol)symbol, node);
      } else {
        return GetTypeName(symbol);
      }
    }

    public override LuaSyntaxNode VisitOmittedArraySizeExpression(OmittedArraySizeExpressionSyntax node) {
      return null;
    }

    public override LuaSyntaxNode VisitArrayRankSpecifier(ArrayRankSpecifierSyntax node) {
      LuaArrayRankSpecifierSyntax rankSpecifier = new LuaArrayRankSpecifierSyntax(node.Rank);
      foreach (var size in node.Sizes) {
        var expression = (LuaExpressionSyntax)size.Accept(this);
        rankSpecifier.Sizes.Add(expression);
      }
      return rankSpecifier;
    }

    public override LuaSyntaxNode VisitArrayType(ArrayTypeSyntax node) {
      var elementType = (LuaExpressionSyntax)node.ElementType.Accept(this);

      LuaInvocationExpressionSyntax typeExpress = null;
      foreach (var rank in node.RankSpecifiers.Reverse()) {
        var arrayTypeName = rank.Rank == 1 ? LuaIdentifierNameSyntax.Array : LuaIdentifierNameSyntax.MultiArray;
        typeExpress = new LuaInvocationExpressionSyntax(arrayTypeName, typeExpress ?? elementType);
      }

      var arrayRankSpecifier = (LuaArrayRankSpecifierSyntax)node.RankSpecifiers[0].Accept(this);
      LuaArrayTypeAdapterExpressionSyntax arrayTypeAdapter = new LuaArrayTypeAdapterExpressionSyntax(typeExpress, arrayRankSpecifier);
      return arrayTypeAdapter;
    }

    private void FillMultiArrayInitializer(InitializerExpressionSyntax initializer, LuaTableInitializerExpression rankSpecifier, LuaInvocationExpressionSyntax invocation, bool isFirst) {
      if (isFirst) {
        rankSpecifier.Items.Add(new LuaSingleTableItemSyntax(new LuaIdentifierNameSyntax(initializer.Expressions.Count)));
      }

      int index = 0;
      foreach (var expression in initializer.Expressions) {
        if (expression.IsKind(SyntaxKind.ArrayInitializerExpression)) {
          FillMultiArrayInitializer((InitializerExpressionSyntax)expression, rankSpecifier, invocation, index == 0);
        } else {
          var item = (LuaExpressionSyntax)expression.Accept(this);
          invocation.AddArgument(item);
        }
        ++index;
      }
    }

    private LuaExpressionSyntax BuildArrayCreationExpression(LuaArrayTypeAdapterExpressionSyntax arrayType, InitializerExpressionSyntax initializer) {
      if (initializer != null && initializer.Expressions.Count > 0) {
        if (arrayType.IsSimapleArray) {
          return new LuaInvocationExpressionSyntax(arrayType, initializer.Expressions.Select(i => (LuaExpressionSyntax)i.Accept(this)));
        } else {
          LuaTableInitializerExpression rankSpecifier = new LuaTableInitializerExpression();
          LuaInvocationExpressionSyntax invocationExpression = new LuaInvocationExpressionSyntax(arrayType, rankSpecifier);
          FillMultiArrayInitializer(initializer, rankSpecifier, invocationExpression, true);
          return invocationExpression;
        }
      } else {
        if (arrayType.IsSimapleArray) {
          var size = arrayType.RankSpecifier.Sizes[0];
          if (size == null) {
            return BuildEmptyArray(arrayType.BaseType);
          }

          if (size is LuaLiteralExpressionSyntax constSize && constSize.Text == 0.ToString()) {
            return BuildEmptyArray(arrayType.BaseType);
          }

          LuaMemberAccessExpressionSyntax memberAccess = new LuaMemberAccessExpressionSyntax(arrayType, LuaIdentifierNameSyntax.New, true);
          return new LuaInvocationExpressionSyntax(memberAccess, size);
        } else {
          LuaTableInitializerExpression rankSpecifier = new LuaTableInitializerExpression();
          foreach (var size in arrayType.RankSpecifier.Sizes) {
            if (size != null) {
              rankSpecifier.Items.Add(new LuaSingleTableItemSyntax(size));
            } else {
              rankSpecifier.Items.Add(new LuaSingleTableItemSyntax(LuaIdentifierLiteralExpressionSyntax.Zero));
            }
          }
          return new LuaInvocationExpressionSyntax(arrayType, rankSpecifier);
        }
      }
    }

    public override LuaSyntaxNode VisitArrayCreationExpression(ArrayCreationExpressionSyntax node) {
      var arrayType = (LuaArrayTypeAdapterExpressionSyntax)node.Type.Accept(this);
      return BuildArrayCreationExpression(arrayType, node.Initializer);
    }

    private LuaArrayTypeAdapterExpressionSyntax BuildArrayTypeFromInitializer(InitializerExpressionSyntax initializer) {
      int rank = 1;
      var firstExpression = initializer.Expressions.First();
      if (firstExpression.IsKind(SyntaxKind.ArrayInitializerExpression)) {
        do {
          ++rank;
          firstExpression = ((InitializerExpressionSyntax)firstExpression).Expressions.First();
        } while (firstExpression.IsKind(SyntaxKind.ArrayInitializerExpression));
      }
      var symbol = semanticModel_.GetTypeInfo(firstExpression).Type;
      LuaExpressionSyntax elementType = GetTypeName(symbol);
      LuaInvocationExpressionSyntax typeExpress = new LuaInvocationExpressionSyntax(rank == 1 ? LuaIdentifierNameSyntax.Array : LuaIdentifierNameSyntax.MultiArray, elementType);
      return new LuaArrayTypeAdapterExpressionSyntax(typeExpress, new LuaArrayRankSpecifierSyntax(rank));
    }

    public override LuaSyntaxNode VisitImplicitArrayCreationExpression(ImplicitArrayCreationExpressionSyntax node) {
      var arrayType = BuildArrayTypeFromInitializer(node.Initializer);
      return BuildArrayCreationExpression(arrayType, node.Initializer);
    }

    private LuaInvocationExpressionSyntax BuildCallBaseConstructor(INamedTypeSymbol type, ITypeSymbol baseType, int ctroCounter) {
      var typeName = !generator_.IsSealed(type) ? GetTypeName(baseType) : new LuaMemberAccessExpressionSyntax(LuaIdentifierNameSyntax.This, LuaIdentifierNameSyntax.Base);
      LuaMemberAccessExpressionSyntax memberAccess = new LuaMemberAccessExpressionSyntax(typeName, LuaIdentifierNameSyntax.Ctor);
      LuaInvocationExpressionSyntax otherCtorInvoke;
      if (ctroCounter > 0) {
        otherCtorInvoke = new LuaInvocationExpressionSyntax(new LuaTableIndexAccessExpressionSyntax(memberAccess, new LuaIdentifierNameSyntax(ctroCounter)));
      } else {
        otherCtorInvoke = new LuaInvocationExpressionSyntax(memberAccess);
      }
      return otherCtorInvoke;
    }

    private LuaInvocationExpressionSyntax BuildCallBaseConstructor(INamedTypeSymbol typeSymbol) {
      var baseType = typeSymbol.BaseType;
      if (baseType != null && baseType.SpecialType != SpecialType.System_Object && baseType.SpecialType != SpecialType.System_ValueType) {
        int ctroCounter = 0;
        if (baseType.IsFromCode()) {
          if (baseType.Constructors.Count(i => !i.IsStatic) > 1) {
            ctroCounter = 1;
          }
        }
        var otherCtorInvoke = BuildCallBaseConstructor(typeSymbol, baseType, ctroCounter);
        otherCtorInvoke.AddArgument(LuaIdentifierNameSyntax.This);
        return otherCtorInvoke;
      }
      return null;
    }

    public override LuaSyntaxNode VisitConstructorDeclaration(ConstructorDeclarationSyntax node) {
      IMethodSymbol ctorSymbol = semanticModel_.GetDeclaredSymbol(node);
      methodInfos_.Push(new MethodInfo(ctorSymbol));

      LuaConstructorAdapterExpressionSyntax function = new LuaConstructorAdapterExpressionSyntax();
      PushFunction(function);
      bool isStatic = node.Modifiers.IsStatic();
      function.IsStatic = isStatic;
      function.AddParameter(LuaIdentifierNameSyntax.This);
      var parameterList = (LuaParameterListSyntax)node.ParameterList.Accept(this);
      function.ParameterList.Parameters.AddRange(parameterList.Parameters);

      if (node.Initializer != null) {
        var symbol = (IMethodSymbol)semanticModel_.GetSymbolInfo(node.Initializer).Symbol;
        int ctroCounter = GetConstructorIndex(symbol);
        LuaInvocationExpressionSyntax otherCtorInvoke;
        if (node.Initializer.IsKind(SyntaxKind.ThisConstructorInitializer)) {
          Contract.Assert(ctroCounter != 0);
          LuaIdentifierNameSyntax thisCtor = new LuaIdentifierNameSyntax(LuaSyntaxNode.SpecailWord(LuaSyntaxNode.Tokens.Ctor + ctroCounter));
          otherCtorInvoke = new LuaInvocationExpressionSyntax(thisCtor);
          function.IsInvokeThisCtor = true;
        } else {
          otherCtorInvoke = BuildCallBaseConstructor(ctorSymbol.ContainingType, symbol.ReceiverType, ctroCounter);
        }
        otherCtorInvoke.AddArgument(LuaIdentifierNameSyntax.This);
        var arguments = BuildArgumentList(symbol, symbol.Parameters, node.Initializer.ArgumentList);
        otherCtorInvoke.AddArguments(arguments);
        function.AddStatement(otherCtorInvoke);
      } else if (!isStatic) {
        var baseCtorInvoke = BuildCallBaseConstructor(ctorSymbol.ContainingType);
        if (baseCtorInvoke != null) {
          function.AddStatement(baseCtorInvoke);
        }
      }

      if (node.Body != null) {
        LuaBlockSyntax block = (LuaBlockSyntax)node.Body.Accept(this);
        function.AddStatements(block.Statements);
      } else {
        blocks_.Push(function.Body);
        var bodyExpression = (LuaExpressionSyntax)node.ExpressionBody.Accept(this);
        function.AddStatement(bodyExpression);
        blocks_.Pop();
      }

      PopFunction();
      if (isStatic) {
        CurType.SetStaticCtor(function);
      } else {
        CurType.AddCtor(function, node.ParameterList.Parameters.Count == 0);
      }

      methodInfos_.Pop();
      return function;
    }

    public override LuaSyntaxNode VisitSimpleBaseType(SimpleBaseTypeSyntax node) {
      return node.Type.Accept(this);
    }

    private LuaExpressionSyntax VisitLambdaExpression(IEnumerable<ParameterSyntax> parameters, CSharpSyntaxNode body) {
      IMethodSymbol symbol = (IMethodSymbol)semanticModel_.GetSymbolInfo(body.Parent).Symbol;
      methodInfos_.Push(new MethodInfo(symbol));

      LuaFunctionExpressionSyntax function = new LuaFunctionExpressionSyntax();
      PushFunction(function);

      if (parameters != null) {
        foreach (var parameter in parameters) {
          var luaParameter = (LuaParameterSyntax)parameter.Accept(this);
          function.ParameterList.Parameters.Add(luaParameter);
        }
      }

      LuaExpressionSyntax resultExpression = function;
      if (body.IsKind(SyntaxKind.Block)) {
        var block = (LuaBlockSyntax)body.Accept(this);
        function.AddStatements(block.Statements);
      } else {
        var type = (INamedTypeSymbol)semanticModel_.GetTypeInfo(body.Parent).ConvertedType;
        var delegateInvokeMethod = type.DelegateInvokeMethod;

        blocks_.Push(function.Body);
        var expression = (LuaExpressionSyntax)body.Accept(this);
        blocks_.Pop();
        if (delegateInvokeMethod.ReturnsVoid) {
          function.AddStatement(expression);
        } else {
          function.AddStatement(new LuaReturnStatementSyntax(expression));
        }
      }

      PopFunction();
      methodInfos_.Pop();

      return resultExpression;
    }

    public override LuaSyntaxNode VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node) {
      return VisitLambdaExpression(new ParameterSyntax[] { node.Parameter }, node.Body);
    }

    public override LuaSyntaxNode VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node) {
      return VisitLambdaExpression(node.ParameterList.Parameters, node.Body);
    }

    public override LuaSyntaxNode VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node) {
      return VisitLambdaExpression(node.ParameterList?.Parameters, node.Body);
    }

    public override LuaSyntaxNode VisitTypeParameter(TypeParameterSyntax node) {
      return new LuaIdentifierNameSyntax(node.Identifier.ValueText);
    }

    public override LuaSyntaxNode VisitTypeOfExpression(TypeOfExpressionSyntax node) {
      var type = semanticModel_.GetTypeInfo(node.Type).Type;
      if (type != null && type.TypeKind == TypeKind.Enum) {
        AddExportEnum(type);
        var typeNameExpression = GetTypeShortName(type);
        return new LuaInvocationExpressionSyntax(LuaIdentifierNameSyntax.TypeOf, typeNameExpression);
      }

      var typeName = (LuaExpressionSyntax)node.Type.Accept(this);
      return new LuaInvocationExpressionSyntax(LuaIdentifierNameSyntax.TypeOf, typeName);
    }

    public override LuaSyntaxNode VisitThrowStatement(ThrowStatementSyntax node) {
      LuaInvocationExpressionSyntax invocationExpression = new LuaInvocationExpressionSyntax(LuaIdentifierNameSyntax.Throw);
      if (node.Expression != null) {
        var expression = (LuaExpressionSyntax)node.Expression.Accept(this);
        invocationExpression.AddArgument(expression);
      } else {
        var curTryFunction = (LuaTryAdapterExpressionSyntax)CurFunction;
        Contract.Assert(curTryFunction.CatchTemp != null);
        invocationExpression.AddArgument(curTryFunction.CatchTemp);
      }
      return new LuaExpressionStatementSyntax(invocationExpression);
    }

    public override LuaSyntaxNode VisitThrowExpression(ThrowExpressionSyntax node) {
      LuaInvocationExpressionSyntax invocationExpression = new LuaInvocationExpressionSyntax(LuaIdentifierNameSyntax.Throw);
      var expression = (LuaExpressionSyntax)node.Expression.Accept(this);
      invocationExpression.AddArgument(expression);
      return invocationExpression;
    }

    public override LuaSyntaxNode VisitCatchFilterClause(CatchFilterClauseSyntax node) {
      return node.FilterExpression.Accept(this);
    }

    public override LuaSyntaxNode VisitCatchClause(CatchClauseSyntax node) {
      throw new InvalidOperationException();
    }

    public override LuaSyntaxNode VisitCatchDeclaration(CatchDeclarationSyntax node) {
      return new LuaVariableDeclaratorSyntax(new LuaIdentifierNameSyntax(node.Identifier.ValueText));
    }

    private LuaTryAdapterExpressionSyntax VisitTryCatchesExpress(SyntaxList<CatchClauseSyntax> catches) {
      LuaTryAdapterExpressionSyntax functionExpress = new LuaTryAdapterExpressionSyntax();
      PushFunction(functionExpress);
      var temp = GetTempIdentifier(catches.First());
      functionExpress.CatchTemp = temp;
      functionExpress.AddParameter(temp);

      LuaIfStatementSyntax ifStatement = null;
      bool hasCatchRoot = false;
      foreach (var catchNode in catches) {
        bool isRootExceptionDeclaration = false;
        LuaExpressionSyntax ifCondition = null;
        if (catchNode.Filter != null) {
          ifCondition = (LuaExpressionSyntax)catchNode.Filter.Accept(this);
        }
        if (catchNode.Declaration != null) {
          var typeName = (LuaIdentifierNameSyntax)catchNode.Declaration.Type.Accept(this);
          if (typeName.ValueText != "System.Exception") {
            var mathcTypeInvocation = new LuaInvocationExpressionSyntax(LuaIdentifierNameSyntax.Is, temp, typeName);
            if (ifCondition != null) {
              ifCondition = new LuaBinaryExpressionSyntax(ifCondition, LuaSyntaxNode.Tokens.And, mathcTypeInvocation);
            } else {
              ifCondition = mathcTypeInvocation;
            }
          } else {
            if (!catchNode.Declaration.Identifier.IsKind(SyntaxKind.None)) {
              isRootExceptionDeclaration = true;
            }
            hasCatchRoot = true;
          }
        } else {
          hasCatchRoot = true;
        }

        var block = (LuaBlockSyntax)catchNode.Block.Accept(this);
        if (ifCondition != null) {
          LuaBlockSyntax body;
          if (ifStatement == null) {
            ifStatement = new LuaIfStatementSyntax(ifCondition);
            body = ifStatement.Body;
          } else {
            LuaElseIfStatementSyntax elseIfStatement = new LuaElseIfStatementSyntax(ifCondition);
            body = elseIfStatement.Body;
            ifStatement.ElseIfStatements.Add(elseIfStatement);
          }
          if (catchNode.Declaration != null && !catchNode.Declaration.Identifier.IsKind(SyntaxKind.None)) {
            var variableDeclarator = (LuaVariableDeclaratorSyntax)catchNode.Declaration.Accept(this);
            variableDeclarator.Initializer = new LuaEqualsValueClauseSyntax(temp);
            body.Statements.Add(new LuaLocalVariableDeclaratorSyntax(variableDeclarator));
          }
          body.Statements.AddRange(block.Statements);
        } else {
          if (isRootExceptionDeclaration) {
            var variableDeclarator = (LuaVariableDeclaratorSyntax)catchNode.Declaration.Accept(this);
            variableDeclarator.Initializer = new LuaEqualsValueClauseSyntax(temp);
            block.Statements.Insert(0, new LuaLocalVariableDeclaratorSyntax(variableDeclarator));
          }

          if (ifStatement != null) {
            LuaElseClauseSyntax elseClause = new LuaElseClauseSyntax();
            elseClause.Body.Statements.AddRange(block.Statements);
            ifStatement.Else = elseClause;
          } else {
            functionExpress.AddStatements(block.Statements);
          }
          break;
        }
      }

      if (ifStatement != null) {
        if (!hasCatchRoot) {
          Contract.Assert(ifStatement.Else == null);
          LuaMultipleReturnStatementSyntax rethrowStatement = new LuaMultipleReturnStatementSyntax();
          rethrowStatement.Expressions.Add(LuaIdentifierNameSyntax.One);
          rethrowStatement.Expressions.Add(temp);
          LuaBlockSyntax block = new LuaBlockSyntax();
          block.Statements.Add(rethrowStatement);
          LuaElseClauseSyntax elseClause = new LuaElseClauseSyntax();
          elseClause.Body.Statements.AddRange(block.Statements);
          ifStatement.Else = elseClause;
        }
        functionExpress.AddStatement(ifStatement);
      }

      PopFunction();
      return functionExpress;
    }

    private LuaStatementSyntax BuildCheckReturnInvocationExpression(LuaInvocationExpressionSyntax invocationExpression, SyntaxNode node) {
      if (IsReturnExists(node)) {
        var curMethodInfo = CurMethodInfoOrNull;
        bool isReturnVoid = curMethodInfo != null && curMethodInfo.Symbol.ReturnsVoid;

        var temp1 = GetTempIdentifier(node);
        var temp2 = isReturnVoid ? null : GetTempIdentifier(node);
        LuaLocalVariablesStatementSyntax localVariables = new LuaLocalVariablesStatementSyntax();
        localVariables.Variables.Add(temp1);
        if (temp2 != null) {
          localVariables.Variables.Add(temp2);
        }
        LuaEqualsValueClauseListSyntax initializer = new LuaEqualsValueClauseListSyntax();
        initializer.Values.Add(invocationExpression);
        localVariables.Initializer = initializer;

        LuaIfStatementSyntax ifStatement = new LuaIfStatementSyntax(temp1);
        if (CurFunction is LuaCheckReturnFunctionExpressionSyntax) {
          LuaMultipleReturnStatementSyntax returnStatement = new LuaMultipleReturnStatementSyntax();
          returnStatement.Expressions.Add(LuaIdentifierNameSyntax.True);
          if (temp2 != null) {
            returnStatement.Expressions.Add(temp2);
          }
          ifStatement.Body.Statements.Add(returnStatement);
        } else {
          if (curMethodInfo != null && curMethodInfo.RefOrOutParameters.Count > 0) {
            LuaMultipleReturnStatementSyntax returnStatement = new LuaMultipleReturnStatementSyntax();
            if (temp2 != null) {
              returnStatement.Expressions.Add(temp2);
            }
            returnStatement.Expressions.AddRange(curMethodInfo.RefOrOutParameters);
            ifStatement.Body.Statements.Add(returnStatement);
          } else {
            ifStatement.Body.Statements.Add(new LuaReturnStatementSyntax(temp2));
          }
        }

        LuaStatementListSyntax statements = new LuaStatementListSyntax();
        statements.Statements.Add(localVariables);
        statements.Statements.Add(ifStatement);
        return statements;
      } else {
        return new LuaExpressionStatementSyntax(invocationExpression);
      }
    }

    public override LuaSyntaxNode VisitFinallyClause(FinallyClauseSyntax node) {
      LuaFunctionExpressionSyntax functionExpress = new LuaFunctionExpressionSyntax();
      PushFunction(functionExpress);
      var finallyBlock = (LuaBlockSyntax)node.Block.Accept(this);
      PopFunction();
      functionExpress.AddStatements(finallyBlock.Statements);
      return functionExpress;
    }

    public override LuaSyntaxNode VisitTryStatement(TryStatementSyntax node) {
      LuaInvocationExpressionSyntax tryInvocationExpression = new LuaInvocationExpressionSyntax(LuaIdentifierNameSyntax.Try);

      LuaTryAdapterExpressionSyntax tryBlockFunctionExpress = new LuaTryAdapterExpressionSyntax();
      PushFunction(tryBlockFunctionExpress);
      var block = (LuaBlockSyntax)node.Block.Accept(this);
      PopFunction();
      tryBlockFunctionExpress.AddStatements(block.Statements);
      tryInvocationExpression.AddArgument(tryBlockFunctionExpress);

      if (node.Catches.Count > 0) {
        var catchesExpress = VisitTryCatchesExpress(node.Catches);
        tryInvocationExpression.AddArgument(catchesExpress);
      } else {
        tryInvocationExpression.AddArgument(LuaIdentifierNameSyntax.Nil);
      }

      if (node.Finally != null) {
        var finallyfunctionExpress = (LuaFunctionExpressionSyntax)node.Finally.Accept(this);
        tryInvocationExpression.AddArgument(finallyfunctionExpress);
      }

      return BuildCheckReturnInvocationExpression(tryInvocationExpression, node);
    }

    public override LuaSyntaxNode VisitUsingStatement(UsingStatementSyntax node) {
      List<LuaIdentifierNameSyntax> variableIdentifiers = new List<LuaIdentifierNameSyntax>();
      List<LuaExpressionSyntax> variableExpressions = new List<LuaExpressionSyntax>();
      if (node.Declaration != null) {
        var variableList = (LuaVariableListDeclarationSyntax)node.Declaration.Accept(this);
        foreach (var variable in variableList.Variables) {
          variableIdentifiers.Add(variable.Identifier);
          variableExpressions.Add(variable.Initializer.Value);
        }
      } else {
        var expression = (LuaExpressionSyntax)node.Expression.Accept(this);
        variableExpressions.Add(expression);
      }

      LuaUsingAdapterExpressionSyntax usingAdapterExpress = new LuaUsingAdapterExpressionSyntax();
      usingAdapterExpress.ParameterList.Parameters.AddRange(variableIdentifiers.Select(i => new LuaParameterSyntax(i)));
      PushFunction(usingAdapterExpress);
      WriteStatementOrBlock(node.Statement, usingAdapterExpress.Body);
      PopFunction();

      if (variableExpressions.Count == 1) {
        LuaInvocationExpressionSyntax usingInvocationExpression = new LuaInvocationExpressionSyntax(LuaIdentifierNameSyntax.Using);
        usingInvocationExpression.AddArgument(variableExpressions.First());
        usingInvocationExpression.AddArgument(usingAdapterExpress);
        return BuildCheckReturnInvocationExpression(usingInvocationExpression, node);
      } else {
        LuaInvocationExpressionSyntax usingInvocationExpression = new LuaInvocationExpressionSyntax(LuaIdentifierNameSyntax.UsingX);
        usingInvocationExpression.AddArgument(usingAdapterExpress);
        usingInvocationExpression.ArgumentList.Arguments.AddRange(variableExpressions.Select(i => new LuaArgumentSyntax(i)));
        return BuildCheckReturnInvocationExpression(usingInvocationExpression, node);
      }
    }

    public override LuaSyntaxNode VisitThisExpression(ThisExpressionSyntax node) {
      return LuaIdentifierNameSyntax.This;
    }

    private enum BaseVisitType {
      UseThis,
      UseName,
      UseBase,
    }

    private BaseVisitType CheckBaseVisitType<T>(MemberAccessExpressionSyntax parent, T symbol, Func<T, ISymbol> overriddenFunc) where T : ISymbol {
      if (symbol.IsOverridable()) {
        var curTypeSymbol = CurTypeSymbol;
        if (generator_.IsSealed(curTypeSymbol)) {
          bool exists = curTypeSymbol.GetMembers().OfType<T>().Any(i => {
            var overriddenSymbol = overriddenFunc(i);
            return overriddenSymbol != null && overriddenSymbol.Equals(symbol);
          });
          return exists ? BaseVisitType.UseBase : BaseVisitType.UseThis;
        } else {
          return BaseVisitType.UseName;
        }
      } else {
        return BaseVisitType.UseThis;
      }
    }

    public override LuaSyntaxNode VisitBaseExpression(BaseExpressionSyntax node) {
      var parent = (MemberAccessExpressionSyntax)node.Parent;
      var symbol = semanticModel_.GetSymbolInfo(parent).Symbol;

      BaseVisitType useType = BaseVisitType.UseThis;
      switch (symbol.Kind) {
        case SymbolKind.Method: {
            symbol.IsOverridable();
            var methodSymbol = (IMethodSymbol)symbol;
            useType = CheckBaseVisitType(parent, methodSymbol, i => i.OverriddenMethod);
            break;
          }
        case SymbolKind.Property: {
            var propertySymbol = (IPropertySymbol)symbol;
            if (!IsPropertyField(propertySymbol)) {
              useType = CheckBaseVisitType(parent, propertySymbol, i => i.OverriddenProperty);
            }
            break;
          }
        case SymbolKind.Event: {
            var eventSymbol = (IEventSymbol)symbol;
            if (!eventSymbol.IsEventFiled()) {
              useType = CheckBaseVisitType(parent, eventSymbol, i => i.OverriddenEvent);
            }
            break;
          }
      }

      switch (useType) {
        case BaseVisitType.UseThis:
          return LuaIdentifierNameSyntax.This;

        case BaseVisitType.UseName:
          return GetTypeName(symbol.ContainingType);

        case BaseVisitType.UseBase:
          return new LuaMemberAccessExpressionSyntax(LuaIdentifierNameSyntax.This, LuaIdentifierNameSyntax.Base);

        default:
          Contract.Assert(false);
          throw new InvalidOperationException();
      }
    }

    public override LuaSyntaxNode VisitConditionalAccessExpression(ConditionalAccessExpressionSyntax node) {
      bool isEmpty = functions_.Count == 0;
      if (isEmpty) {
        LuaFunctionExpressionSyntax function = new LuaFunctionExpressionSyntax();
        PushFunction(function);
        blocks_.Push(function.Body);
      }

      bool isRoot = !node.Parent.IsKind(SyntaxKind.ConditionalAccessExpression);
      if (isRoot) {
        conditionalTemps_.Push(GetTempIdentifier(node.Expression));
      }

      var temp = conditionalTemps_.Peek();
      var expression = (LuaExpressionSyntax)node.Expression.Accept(this);
      if (isRoot) {
        CurBlock.Statements.Add(new LuaLocalVariableDeclaratorSyntax(temp, expression));
      } else {
        CurBlock.Statements.Add(new LuaExpressionStatementSyntax(new LuaAssignmentExpressionSyntax(temp, expression)));
      }

      LuaBinaryExpressionSyntax condition = new LuaBinaryExpressionSyntax(temp, LuaSyntaxNode.Tokens.NotEquals, LuaIdentifierNameSyntax.Nil);
      LuaIfStatementSyntax ifStatement = new LuaIfStatementSyntax(condition);
      CurBlock.Statements.Add(ifStatement);

      blocks_.Push(ifStatement.Body);
      var whenNotNull = (LuaExpressionSyntax)node.WhenNotNull.Accept(this);
      blocks_.Pop();

      if (isRoot) {
        conditionalTemps_.Pop();
      }

      if (node.Parent.IsKind(SyntaxKind.ExpressionStatement)) {
        if (isEmpty) {
          throw new InvalidOperationException();
        }
        if (!node.WhenNotNull.IsKind(SyntaxKind.ConditionalAccessExpression)) {
          ifStatement.Body.Statements.Add(new LuaExpressionStatementSyntax(whenNotNull));
        }
        return LuaExpressionSyntax.EmptyExpression;
      } else {
        if (!node.WhenNotNull.IsKind(SyntaxKind.ConditionalAccessExpression)) {
          LuaAssignmentExpressionSyntax assignment = new LuaAssignmentExpressionSyntax(temp, whenNotNull);
          ifStatement.Body.Statements.Add(new LuaExpressionStatementSyntax(assignment));
        }
        if (isEmpty) {
          var function = CurFunction;
          function.AddStatement(new LuaReturnStatementSyntax(temp));
          blocks_.Pop();
          PopFunction();
          return new LuaInvocationExpressionSyntax(new LuaParenthesizedExpressionSyntax(function));
        } else {
          return temp;
        }
      }
    }

    public override LuaSyntaxNode VisitMemberBindingExpression(MemberBindingExpressionSyntax node) {
      ISymbol symbol = semanticModel_.GetSymbolInfo(node).Symbol;
      if (IsDelegateInvoke(symbol, node.Name)) {
        return conditionalTemps_.Peek();
      }
      var nameExpression = (LuaExpressionSyntax)node.Name.Accept(this);
      return new LuaMemberAccessExpressionSyntax(conditionalTemps_.Peek(), nameExpression, symbol.Kind == SymbolKind.Method);
    }

    public override LuaSyntaxNode VisitElementBindingExpression(ElementBindingExpressionSyntax node) {
      var argumentList = (LuaArgumentListSyntax)node.ArgumentList.Accept(this);
      var memberAccess = new LuaMemberAccessExpressionSyntax(conditionalTemps_.Peek(), new LuaIdentifierNameSyntax(LuaSyntaxNode.Tokens.Get), true);
      LuaInvocationExpressionSyntax invocation = new LuaInvocationExpressionSyntax(memberAccess);
      invocation.ArgumentList.Arguments.AddRange(argumentList.Arguments);
      return invocation;
    }

    public override LuaSyntaxNode VisitDefaultExpression(DefaultExpressionSyntax node) {
      var constExpression = GetConstExpression(node);
      if (constExpression != null) {
        return constExpression;
      }

      var type = semanticModel_.GetTypeInfo(node.Type).Type;
      return GetDefaultValueExpression(type);
    }

    public override LuaSyntaxNode VisitElementAccessExpression(ElementAccessExpressionSyntax node) {
      var symbol = (IPropertySymbol)semanticModel_.GetSymbolInfo(node).Symbol;
      var expression = BuildMemberAccessTargetExpression(node.Expression);
      LuaIdentifierNameSyntax baseName = symbol == null ? LuaIdentifierNameSyntax.Empty : GetMemberName(symbol);
      LuaPropertyOrEventIdentifierNameSyntax identifierName = new LuaPropertyOrEventIdentifierNameSyntax(true, baseName);
      LuaPropertyAdapterExpressionSyntax propertyAdapter = new LuaPropertyAdapterExpressionSyntax(expression, identifierName, true);
      if (symbol != null) {
        List<LuaExpressionSyntax> arguments = BuildArgumentList(symbol, symbol.Parameters, node.ArgumentList);
        propertyAdapter.ArgumentList.AddArguments(arguments);
      } else {
        var argumentList = (LuaArgumentListSyntax)node.ArgumentList.Accept(this);
        propertyAdapter.ArgumentList.Arguments.AddRange(argumentList.Arguments);
      }
      return propertyAdapter;
    }

    public override LuaSyntaxNode VisitInterpolatedStringExpression(InterpolatedStringExpressionSyntax node) {
      int index = 0;
      StringBuilder sb = new StringBuilder();
      List<LuaExpressionSyntax> expressions = new List<LuaExpressionSyntax>();
      foreach (var content in node.Contents) {
        if (content.IsKind(SyntaxKind.InterpolatedStringText)) {
          var stringText = (InterpolatedStringTextSyntax)content;
          sb.Append(stringText.TextToken.ValueText);
        } else {
          var expression = (LuaExpressionSyntax)content.Accept(this);
          expressions.Add(expression);
          sb.Append('{');
          sb.Append(index);
          sb.Append('}');
          ++index;
        }
      }

      LuaLiteralExpressionSyntax format;
      if (node.StringStartToken.ValueText.Contains('@')) {
        format = BuildVerbatimStringExpression(sb.ToString());
      } else {
        format = BuildStringLiteralExpression(sb.ToString());
      }
      LuaMemberAccessExpressionSyntax memberAccessExpression = new LuaMemberAccessExpressionSyntax(new LuaParenthesizedExpressionSyntax(format), LuaIdentifierNameSyntax.Format, true);
      return new LuaInvocationExpressionSyntax(memberAccessExpression, expressions);
    }

    public override LuaSyntaxNode VisitInterpolation(InterpolationSyntax node) {
      return node.Expression.Accept(this);
    }

    public override LuaSyntaxNode VisitInterpolatedStringText(InterpolatedStringTextSyntax node) {
      throw new InvalidOperationException();
    }

    public override LuaSyntaxNode VisitAliasQualifiedName(AliasQualifiedNameSyntax node) {
      return node.Name.Accept(this);
    }

    private void BuildOperatorMethodDeclaration(BaseMethodDeclarationSyntax node) {
      var symbol = semanticModel_.GetDeclaredSymbol(node);
      methodInfos_.Push(new MethodInfo(symbol));

      bool isStatic = symbol.IsStatic;
      bool isPrivate = symbol.IsPrivate();

      LuaIdentifierNameSyntax name = GetMemberName(symbol);
      var parameterList = (LuaParameterListSyntax)node.ParameterList.Accept(this);
      LuaFunctionExpressionSyntax function = new LuaFunctionExpressionSyntax();
      function.ParameterList.Parameters.AddRange(parameterList.Parameters);
      PushFunction(function);

      var comments = BuildDocumentationComment(node);
      LuaBlockSyntax block = (LuaBlockSyntax)node.Body.Accept(this);
      function.AddStatements(block.Statements);
      CurType.AddMethod(name, function, isPrivate, comments);

      PopFunction();
      methodInfos_.Pop();
    }

    public override LuaSyntaxNode VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax node) {
      BuildOperatorMethodDeclaration(node);
      return base.VisitConversionOperatorDeclaration(node);
    }

    public override LuaSyntaxNode VisitOperatorDeclaration(OperatorDeclarationSyntax node) {
      BuildOperatorMethodDeclaration(node);
      return base.VisitOperatorDeclaration(node);
    }

    public override LuaSyntaxNode VisitSizeOfExpression(SizeOfExpressionSyntax node) {
      var constExpression = GetConstExpression(node);
      Contract.Assert(constExpression != null);
      return constExpression;
    }

    public override LuaSyntaxNode VisitStackAllocArrayCreationExpression(StackAllocArrayCreationExpressionSyntax node) {
      var arrayType = (LuaArrayTypeAdapterExpressionSyntax)node.Type.Accept(this);
      Contract.Assert(arrayType.IsSimapleArray);
      return new LuaInvocationExpressionSyntax(LuaIdentifierNameSyntax.StackAlloc, arrayType, arrayType.RankSpecifier.Sizes[0]);
    }

    public override LuaSyntaxNode VisitUnsafeStatement(UnsafeStatementSyntax node) {
      LuaStatementListSyntax statements = new LuaStatementListSyntax();
      statements.Statements.Add(new LuaShortCommentStatement(" " + node.UnsafeKeyword));
      var block = (LuaStatementSyntax)node.Block.Accept(this);
      statements.Statements.Add(block);
      return statements;
    }

    public override LuaSyntaxNode VisitFixedStatement(FixedStatementSyntax node) {
      LuaStatementListSyntax statements = new LuaStatementListSyntax();
      statements.Statements.Add(new LuaShortCommentStatement(" " + node.FixedKeyword));
      LuaBlockStatementSyntax block = new LuaBlockStatementSyntax();
      var declaration = (LuaStatementSyntax)node.Declaration.Accept(this);
      block.Statements.Add(declaration);
      WriteStatementOrBlock(node.Statement, block);
      statements.Statements.Add(block);
      return statements;
    }

    public override LuaSyntaxNode VisitLockStatement(LockStatementSyntax node) {
      LuaStatementListSyntax statements = new LuaStatementListSyntax();
      statements.Statements.Add(new LuaShortCommentStatement($" {node.LockKeyword}({node.Expression})"));
      LuaBlockStatementSyntax block = new LuaBlockStatementSyntax();
      WriteStatementOrBlock(node.Statement, block);
      statements.Statements.Add(block);
      return statements;
    }

    public override LuaSyntaxNode VisitArrowExpressionClause(ArrowExpressionClauseSyntax node) {
      return node.Expression.Accept(this);
    }

    public override LuaSyntaxNode VisitLocalFunctionStatement(LocalFunctionStatementSyntax node) {
      var result = BuildMethodDeclaration(node, default(SyntaxList<AttributeListSyntax>), node.ParameterList, node.TypeParameterList, node.Body, node.ExpressionBody, node.ReturnType);
      var parentNode = FindParent(node, i => i.IsKind(SyntaxKind.MethodDeclaration) || i.IsKind(SyntaxKind.LocalFunctionStatement));
      var body = parentNode.IsKind(SyntaxKind.MethodDeclaration) ? ((MethodDeclarationSyntax)parentNode).Body : ((LocalFunctionStatementSyntax)parentNode).Body;
      bool isOnlyOne = body.Statements.OfType<LocalFunctionStatementSyntax>().Count() == 1;
      if (isOnlyOne) {
        return new LuaLocalFunctionSyntx(result.Name, result.Function, result.Document);
      } else {
        var block = blocks_.Peek();
        block.AddLocalArea(result.Name);
        var localVar = new LuaAssignmentExpressionSyntax(result.Name, result.Function).ToStatement();
        if (result.Document != null && !result.Document.IsEmpty) {
          LuaStatementListSyntax statementList = new LuaStatementListSyntax();
          statementList.Statements.Add(result.Document);
          statementList.Statements.Add(localVar);
          return statementList;
        } else {
          return localVar;
        }
      }
    }

    public override LuaSyntaxNode VisitDeclarationExpression(DeclarationExpressionSyntax node) {
      if (node.Parent.IsKind(SyntaxKind.Argument)) {      //out var 
        var name = (LuaIdentifierNameSyntax)node.Designation.Accept(this);
        blocks_.Peek().Statements.Add(new LuaLocalVariableDeclaratorSyntax(name));
        return name;
      } else {    //ValueTuple deconstruction
        return node.Designation.Accept(this);
      }
    }

    public override LuaSyntaxNode VisitDiscardDesignation(DiscardDesignationSyntax node) {
      return new LuaIdentifierNameSyntax(node.UnderscoreToken.ValueText);
    }

    public override LuaSyntaxNode VisitSingleVariableDesignation(SingleVariableDesignationSyntax node) {
      LuaIdentifierNameSyntax name = new LuaIdentifierNameSyntax(node.Identifier.ValueText);
      CheckLocalVariableName(ref name, node);
      return name;
    }

    public override LuaSyntaxNode VisitIsPatternExpression(IsPatternExpressionSyntax node) {
      var declarationPattern = (DeclarationPatternSyntax)node.Pattern;
      var name = (LuaIdentifierNameSyntax)declarationPattern.Designation.Accept(this);
      var expression = (LuaExpressionSyntax)node.Expression.Accept(this);
      blocks_.Peek().Statements.Add(new LuaLocalVariableDeclaratorSyntax(name, expression));

      var leftType = semanticModel_.GetTypeInfo(node.Expression).Type;
      var rightType = semanticModel_.GetTypeInfo(declarationPattern.Type).Type;
      if (leftType.IsSubclassOf(rightType)) {
        return LuaIdentifierLiteralExpressionSyntax.True;
      } else {
        var type = (LuaExpressionSyntax)declarationPattern.Type.Accept(this);
        return new LuaInvocationExpressionSyntax(LuaIdentifierNameSyntax.Is, name, type);
      }
    }

    public override LuaSyntaxNode VisitDeclarationPattern(DeclarationPatternSyntax node) {
      throw new InvalidOperationException();
    }

    public override LuaSyntaxNode VisitRefExpression(RefExpressionSyntax node) {
      if (node.Expression.IsKind(SyntaxKind.InvocationExpression)) {
        throw new CompilationErrorException(node, "ref returns is not support");
      }
      return node.Expression.Accept(this);
    }

    public override LuaSyntaxNode VisitTupleType(TupleTypeSyntax node) {
      return LuaIdentifierNameSyntax.ValueTupleType;
    }

    private LuaExpressionSyntax BuildValueTupleCreateExpression(IEnumerable<LuaExpressionSyntax> expressions) {
      LuaTableInitializerExpression table = new LuaTableInitializerExpression();
      table.Items.AddRange(expressions.Select(i => new LuaSingleTableItemSyntax(i)));
      return new LuaInvocationExpressionSyntax(LuaIdentifierNameSyntax.ValueTupleTypeCreate, table);
    }

    public override LuaSyntaxNode VisitTupleExpression(TupleExpressionSyntax node) {
      var expressions = node.Arguments.Select(i => (LuaExpressionSyntax)i.Expression.Accept(this));
      if (node.Parent.IsKind(SyntaxKind.SimpleAssignmentExpression)) {
        var assigment = (AssignmentExpressionSyntax)node.Parent;
        if (assigment.Left == node) {
          LuaSequenceListExpressionSyntax list = new LuaSequenceListExpressionSyntax();
          list.Expressions.AddRange(expressions);
          return list;
        }
      }
      return BuildValueTupleCreateExpression(expressions);
    }

    public override LuaSyntaxNode VisitParenthesizedVariableDesignation(ParenthesizedVariableDesignationSyntax node) {
      LuatLocalTupleVariableExpression expression = new LuatLocalTupleVariableExpression();
      expression.Variables.AddRange(node.Variables.Select(i => (LuaIdentifierNameSyntax)i.Accept(this)));
      return expression;
    }
  }
}