using Acornima;
using Acornima.Ast;
using NetServerLogic.Abstractions;

namespace NetServerLogic.Validation;

/// <summary>
/// AST-based pre-flight validator implementing Power Pages <c>RTSL01</c> parity checks using Acornima.
/// </summary>
public sealed class AcornimaScriptValidator : IServerLogicScriptValidator
{
    private static readonly HashSet<string> ProhibitedGlobals = new(StringComparer.Ordinal)
    {
        "console",
        "fetch",
        "XMLHttpRequest",
        "setTimeout",
        "setInterval"
    };

    private static readonly HashSet<string> ProhibitedGlobalThisMembers = new(StringComparer.Ordinal)
    {
        "console",
        "fetch",
        "XMLHttpRequest",
        "setTimeout",
        "setInterval",
        "eval",
        "Function",
        "Proxy",
        "Reflect"
    };

    private static readonly HashSet<string> MetaprogGlobalObjects = new(StringComparer.Ordinal)
    {
        "Proxy",
        "Reflect"
    };

    private static readonly HashSet<string> HandlerNames = new(StringComparer.Ordinal)
    {
        "get",
        "post",
        "put",
        "patch",
        "del"
    };

    private static readonly HashSet<string> ArrayCallbackMethods = new(StringComparer.Ordinal)
    {
        "map",
        "filter",
        "forEach"
    };

    public ScriptValidationResult Validate(string scriptSource, string scriptName)
    {
        if (string.IsNullOrWhiteSpace(scriptSource))
        {
            return new ScriptValidationResult(
            [
                new ScriptValidationDiagnostic(
                    "RTSL01-NO-HANDLER",
                    ScriptValidationSeverity.Error,
                    $"No top-level HTTP verb handler (get, post, put, patch, or del) is defined in '{scriptName}.js'.",
                    1,
                    1)
            ]);
        }

        var parser = new Parser();
        Node astRoot;
        var isModule = false;

        try
        {
            astRoot = parser.ParseScript(scriptSource, scriptName);
        }
        catch (ParseErrorException scriptEx)
        {
            try
            {
                astRoot = parser.ParseModule(scriptSource, scriptName);
                isModule = true;
            }
            catch (ParseErrorException)
            {
                return new ScriptValidationResult(
                [
                    new ScriptValidationDiagnostic(
                        "RTSL01-SYNTAX",
                        ScriptValidationSeverity.Error,
                        $"Syntax error in '{scriptName}.js': {scriptEx.Description ?? scriptEx.Message}",
                        Math.Max(1, scriptEx.LineNumber),
                        Math.Max(1, scriptEx.Column))
                ]);
            }
        }

        var diagnostics = new List<ScriptValidationDiagnostic>();
        var walker = new ValidationWalker(scriptName, diagnostics);
        walker.ValidateProgram(astRoot, isModule);

        return new ScriptValidationResult(diagnostics);
    }

    private sealed class ValidationWalker
    {
        private readonly string _scriptName;
        private readonly List<ScriptValidationDiagnostic> _diagnostics;
        private readonly Stack<HashSet<string>> _scopes = new();

        public ValidationWalker(string scriptName, List<ScriptValidationDiagnostic> diagnostics)
        {
            _scriptName = scriptName;
            _diagnostics = diagnostics;
        }

        public void ValidateProgram(Node root, bool isModule)
        {
            var topLevelBody = root switch
            {
                Script s => s.Body,
                Module m => m.Body,
                _ => default(NodeList<Statement>)
            };

            PushScope();
            CollectHoistedDeclarations(topLevelBody, includeLexical: true);

            var hasHandler = false;

            foreach (var stmt in topLevelBody)
            {
                if (stmt is FunctionDeclaration fn &&
                    fn.Id is not null &&
                    HandlerNames.Contains(fn.Id.Name))
                {
                    hasHandler = true;
                }
                else if (stmt is VariableDeclaration varDecl)
                {
                    foreach (var declarator in varDecl.Declarations)
                    {
                        if (declarator.Id is Identifier id &&
                            HandlerNames.Contains(id.Name) &&
                            declarator.Init is ArrowFunctionExpression or FunctionExpression)
                        {
                            hasHandler = true;
                        }

                        if (varDecl.Kind is VariableDeclarationKind.Const or VariableDeclarationKind.Let &&
                            declarator.Init is ArrowFunctionExpression or FunctionExpression)
                        {
                            var name = declarator.Id is Identifier varId ? varId.Name : "<pattern>";
                            Report(
                                "RTSL_WARN-ARROW-CONST",
                                ScriptValidationSeverity.Warning,
                                $"Top-level '{varDecl.Kind.ToString().ToLowerInvariant()} {name}' initialized to a function expression can trigger Power Pages validator quirks; prefer 'function {name}() {{}}'.",
                                declarator);
                        }
                    }
                }
                else if (isModule && stmt is ExportNamedDeclaration { Declaration: FunctionDeclaration expFn } &&
                         expFn.Id is not null && HandlerNames.Contains(expFn.Id.Name))
                {
                    hasHandler = true;
                }

                Visit(stmt, isBindingContext: false);
            }

            PopScope();

            if (!hasHandler)
            {
                Report(
                    "RTSL01-NO-HANDLER",
                    ScriptValidationSeverity.Error,
                    $"No top-level HTTP verb handler (get, post, put, patch, or del) is defined in '{_scriptName}.js'.",
                    root);
            }
        }

        private void Visit(Node? node, bool isBindingContext)
        {
            if (node is null)
            {
                return;
            }

            switch (node)
            {
                case UnaryExpression unary:
                    if (unary.Operator == Operator.Delete)
                    {
                        Report(
                            "RTSL01-DELETE",
                            ScriptValidationSeverity.Error,
                            "The 'delete' operator is prohibited in Power Pages Server Logic; assign undefined instead.",
                            unary);
                    }
                    Visit(unary.Argument, false);
                    return;

                case WithStatement withStmt:
                    Report(
                        "RTSL01-WITH",
                        ScriptValidationSeverity.Error,
                        "The 'with' statement is prohibited in Power Pages Server Logic.",
                        withStmt);
                    Visit(withStmt.Object, false);
                    Visit(withStmt.Body, false);
                    return;

                case DebuggerStatement dbg:
                    Report(
                        "RTSL01-DEBUGGER",
                        ScriptValidationSeverity.Error,
                        "The 'debugger' statement is prohibited in Power Pages Server Logic.",
                        dbg);
                    return;

                case ImportDeclaration impDecl:
                    Report(
                        "RTSL01-MODULE",
                        ScriptValidationSeverity.Error,
                        "ES module 'import' declarations are not supported in Power Pages Server Logic.",
                        impDecl);
                    return;

                case ImportExpression impExpr:
                    Report(
                        "RTSL01-MODULE",
                        ScriptValidationSeverity.Error,
                        "Dynamic 'import()' expressions are not supported in Power Pages Server Logic.",
                        impExpr);
                    Visit(impExpr.Source, false);
                    return;

                case ExportNamedDeclaration expNamed:
                    Report(
                        "RTSL01-MODULE",
                        ScriptValidationSeverity.Error,
                        "ES module 'export' declarations are not supported in Power Pages Server Logic.",
                        expNamed);
                    if (expNamed.Declaration is not null)
                    {
                        Visit(expNamed.Declaration, false);
                    }
                    return;

                case ExportDefaultDeclaration expDefault:
                    Report(
                        "RTSL01-MODULE",
                        ScriptValidationSeverity.Error,
                        "ES module 'export default' declarations are not supported in Power Pages Server Logic.",
                        expDefault);
                    Visit(expDefault.Declaration, false);
                    return;

                case ExportAllDeclaration expAll:
                    Report(
                        "RTSL01-MODULE",
                        ScriptValidationSeverity.Error,
                        "ES module 'export *' declarations are not supported in Power Pages Server Logic.",
                        expAll);
                    return;

                case AwaitExpression awaitExpr:
                    if (IsDataverseConnectorCall(awaitExpr.Argument))
                    {
                        Report(
                            "RTSL01-SYNC-AWAIT",
                            ScriptValidationSeverity.Error,
                            "'Server.Connector.Dataverse' methods are synchronous and must not be awaited.",
                            awaitExpr);
                    }
                    Visit(awaitExpr.Argument, false);
                    return;

                case CallExpression callExpr:
                    VisitCallExpression(callExpr);
                    return;

                case NewExpression newExpr:
                    VisitNewExpression(newExpr);
                    return;

                case MemberExpression memberExpr:
                    VisitMemberExpression(memberExpr);
                    return;

                case Identifier id:
                    if (!isBindingContext)
                    {
                        CheckStandaloneIdentifier(id);
                    }
                    return;

                case VariableDeclaration varDecl:
                    foreach (var declarator in varDecl.Declarations)
                    {
                        VisitPatternBindingsOnly(declarator.Id);
                        if (declarator.Init is not null)
                        {
                            Visit(declarator.Init, false);
                        }
                    }
                    return;

                case FunctionDeclaration fnDecl:
                    VisitFunction(fnDecl.Id, fnDecl.Params, fnDecl.Body, declareIdInCurrentScope: false);
                    return;

                case FunctionExpression fnExpr:
                    VisitFunction(fnExpr.Id, fnExpr.Params, fnExpr.Body, declareIdInCurrentScope: false);
                    return;

                case ArrowFunctionExpression arrowExpr:
                    VisitFunction(null, arrowExpr.Params, arrowExpr.Body, declareIdInCurrentScope: false);
                    return;

                case BlockStatement block:
                    PushScope();
                    CollectLexicalDeclarations(block.Body);
                    foreach (var child in block.Body)
                    {
                        Visit(child, false);
                    }
                    PopScope();
                    return;

                case ForStatement forStmt:
                    PushScope();
                    if (forStmt.Init is VariableDeclaration forVar &&
                        forVar.Kind is VariableDeclarationKind.Let or VariableDeclarationKind.Const)
                    {
                        AddPatternNames(forVar);
                    }
                    Visit(forStmt.Init, false);
                    Visit(forStmt.Test, false);
                    Visit(forStmt.Update, false);
                    Visit(forStmt.Body, false);
                    PopScope();
                    return;

                case ForInStatement forIn:
                    PushScope();
                    if (forIn.Left is VariableDeclaration forInVar &&
                        forInVar.Kind is VariableDeclarationKind.Let or VariableDeclarationKind.Const)
                    {
                        AddPatternNames(forInVar);
                    }
                    Visit(forIn.Left, false);
                    Visit(forIn.Right, false);
                    Visit(forIn.Body, false);
                    PopScope();
                    return;

                case ForOfStatement forOf:
                    PushScope();
                    if (forOf.Left is VariableDeclaration forOfVar &&
                        forOfVar.Kind is VariableDeclarationKind.Let or VariableDeclarationKind.Const)
                    {
                        AddPatternNames(forOfVar);
                    }
                    Visit(forOf.Left, false);
                    Visit(forOf.Right, false);
                    Visit(forOf.Body, false);
                    PopScope();
                    return;

                case CatchClause catchClause:
                    PushScope();
                    if (catchClause.Param is not null)
                    {
                        AddPatternNames(catchClause.Param);
                        VisitPatternBindingsOnly(catchClause.Param);
                    }
                    Visit(catchClause.Body, false);
                    PopScope();
                    return;

                case ObjectProperty prop:
                    if (prop.Computed)
                    {
                        Visit(prop.Key, false);
                    }
                    Visit(prop.Value, prop.Shorthand && isBindingContext);
                    return;

                case AssignmentProperty assignProp:
                    if (assignProp.Computed)
                    {
                        Visit(assignProp.Key, false);
                    }
                    Visit(assignProp.Value, isBindingContext);
                    return;

                case MethodDefinition methodDef:
                    if (methodDef.Computed)
                    {
                        Visit(methodDef.Key, false);
                    }
                    Visit(methodDef.Value, false);
                    return;

                case PropertyDefinition propDef:
                    if (propDef.Computed)
                    {
                        Visit(propDef.Key, false);
                    }
                    if (propDef.Value is not null)
                    {
                        Visit(propDef.Value, false);
                    }
                    return;

                case LabeledStatement labeled:
                    Visit(labeled.Body, false);
                    return;

                case BreakStatement:
                case ContinueStatement:
                    return;

                case ClassDeclaration classDecl:
                    if (classDecl.Id is not null)
                    {
                        AddDeclaredName(classDecl.Id.Name);
                    }
                    Visit(classDecl.SuperClass, false);
                    Visit(classDecl.Body, false);
                    return;

                case ClassExpression classExpr:
                    PushScope();
                    if (classExpr.Id is not null)
                    {
                        AddDeclaredName(classExpr.Id.Name);
                    }
                    Visit(classExpr.SuperClass, false);
                    Visit(classExpr.Body, false);
                    PopScope();
                    return;

                default:
                    foreach (var child in node.ChildNodes)
                    {
                        Visit(child, false);
                    }
                    return;
            }
        }

        private void VisitFunction(
            Identifier? id,
            NodeList<Node> parameters,
            Node body,
            bool declareIdInCurrentScope)
        {
            if (declareIdInCurrentScope && id is not null)
            {
                AddDeclaredName(id.Name);
            }

            PushScope();
            if (id is not null)
            {
                AddDeclaredName(id.Name);
            }

            foreach (var param in parameters)
            {
                AddPatternNames(param);
                VisitPatternBindingsOnly(param);
            }

            if (body is FunctionBody fnBody)
            {
                CollectHoistedDeclarations(fnBody.Body, includeLexical: true);
                foreach (var stmt in fnBody.Body)
                {
                    Visit(stmt, false);
                }
            }
            else
            {
                Visit(body, false);
            }

            PopScope();
        }

        private void VisitPatternBindingsOnly(Node pattern)
        {
            switch (pattern)
            {
                case Identifier:
                    return;
                case ObjectPattern objPat:
                    foreach (var prop in objPat.Properties)
                    {
                        if (prop is AssignmentProperty ap)
                        {
                            if (ap.Computed)
                            {
                                Visit(ap.Key, false);
                            }
                            VisitPatternBindingsOnly(ap.Value);
                        }
                        else if (prop is RestElement rest)
                        {
                            VisitPatternBindingsOnly(rest.Argument);
                        }
                    }
                    return;
                case ArrayPattern arrPat:
                    foreach (var elem in arrPat.Elements)
                    {
                        if (elem is not null)
                        {
                            VisitPatternBindingsOnly(elem);
                        }
                    }
                    return;
                case AssignmentPattern assignPat:
                    VisitPatternBindingsOnly(assignPat.Left);
                    Visit(assignPat.Right, false);
                    return;
                case RestElement restElem:
                    VisitPatternBindingsOnly(restElem.Argument);
                    return;
                default:
                    Visit(pattern, true);
                    return;
            }
        }

        private void VisitCallExpression(CallExpression call)
        {
            var callee = UnwrapChain(call.Callee);

            if (callee is Identifier id)
            {
                if (id.Name == "require" && !IsShadowed("require"))
                {
                    Report(
                        "RTSL01-MODULE",
                        ScriptValidationSeverity.Error,
                        "CommonJS 'require(...)' is not supported in Power Pages Server Logic.",
                        call);
                }
                else if (id.Name == "eval" && !IsShadowed("eval"))
                {
                    Report(
                        "RTSL01-DYNAMIC-CODE",
                        ScriptValidationSeverity.Error,
                        "Dynamic code execution via 'eval(...)' is prohibited in Power Pages Server Logic.",
                        call);
                }
                else if (id.Name == "Function" && !IsShadowed("Function"))
                {
                    Report(
                        "RTSL01-DYNAMIC-CODE",
                        ScriptValidationSeverity.Error,
                        "Dynamic code execution via 'Function(...)' is prohibited in Power Pages Server Logic.",
                        call);
                }
            }
            else if (callee is MemberExpression member &&
                     TryGetStaticPropertyName(member, out var methodName) &&
                     ArrayCallbackMethods.Contains(methodName))
            {
                foreach (var arg in call.Arguments)
                {
                    if (arg is ArrowFunctionExpression or FunctionExpression)
                    {
                        Report(
                            "RTSL_WARN-ARRAY-CALLBACK",
                            ScriptValidationSeverity.Warning,
                            $"Inline function callback passed to '.{methodName}()' may trigger Power Pages validator quirks in some builds; prefer a 'for' loop if needed.",
                            arg);
                        break;
                    }
                }
            }

            Visit(call.Callee, false);
            foreach (var arg in call.Arguments)
            {
                Visit(arg, false);
            }
        }

        private void VisitNewExpression(NewExpression newExpr)
        {
            var callee = UnwrapChain(newExpr.Callee);
            if (callee is Identifier id && id.Name == "Function" && !IsShadowed("Function"))
            {
                Report(
                    "RTSL01-DYNAMIC-CODE",
                    ScriptValidationSeverity.Error,
                    "Dynamic code execution via 'new Function(...)' is prohibited in Power Pages Server Logic.",
                    newExpr);
            }
            else if (callee is MemberExpression member &&
                     member.Object is Identifier root &&
                     root.Name == "globalThis" &&
                     !IsShadowed("globalThis") &&
                     TryGetStaticPropertyName(member, out var memberName) &&
                     memberName == "Function")
            {
                Report(
                    "RTSL01-DYNAMIC-CODE",
                    ScriptValidationSeverity.Error,
                    "Dynamic code execution via 'new globalThis.Function(...)' is prohibited in Power Pages Server Logic.",
                    newExpr);
            }

            Visit(newExpr.Callee, false);
            foreach (var arg in newExpr.Arguments)
            {
                Visit(arg, false);
            }
        }

        private void VisitMemberExpression(MemberExpression member)
        {
            if (TryGetStaticPropertyName(member, out var propName))
            {
                if (propName is "prototype" or "__proto__")
                {
                    Report(
                        "RTSL01-METAPROG",
                        ScriptValidationSeverity.Error,
                        $"Access to '{propName}' is prohibited in Power Pages Server Logic.",
                        member);
                }
                else if (propName == "callee" &&
                         member.Object is Identifier argsId &&
                         argsId.Name == "arguments")
                {
                    Report(
                        "RTSL01-METAPROG",
                        ScriptValidationSeverity.Error,
                        "Access to 'arguments.callee' is prohibited in Power Pages Server Logic.",
                        member);
                }
                else if (propName == "for" &&
                         member.Object is Identifier symId &&
                         symId.Name == "Symbol" &&
                         !IsShadowed("Symbol"))
                {
                    Report(
                        "RTSL01-METAPROG",
                        ScriptValidationSeverity.Error,
                        "Access to 'Symbol.for' is prohibited in Power Pages Server Logic.",
                        member);
                }
                else if (propName is "getPrototypeOf" or "setPrototypeOf" &&
                         member.Object is Identifier objId &&
                         objId.Name == "Object" &&
                         !IsShadowed("Object"))
                {
                    Report(
                        "RTSL01-METAPROG",
                        ScriptValidationSeverity.Error,
                        $"Access to 'Object.{propName}' is prohibited in Power Pages Server Logic.",
                        member);
                }
                else if (propName == "constructor")
                {
                    if (member.Object is ThisExpression)
                    {
                        Report(
                            "RTSL01-METAPROG",
                            ScriptValidationSeverity.Error,
                            "Access to 'this.constructor' is prohibited in Power Pages Server Logic.",
                            member);
                    }
                    else if (member.Object is MemberExpression inner &&
                             TryGetStaticPropertyName(inner, out var innerProp) &&
                             innerProp == "constructor")
                    {
                        Report(
                            "RTSL01-METAPROG",
                            ScriptValidationSeverity.Error,
                            "Access to 'constructor.constructor' is prohibited in Power Pages Server Logic.",
                            member);
                    }
                }
                else if (member.Object is Identifier globalThisId &&
                         globalThisId.Name == "globalThis" &&
                         !IsShadowed("globalThis") &&
                         ProhibitedGlobalThisMembers.Contains(propName))
                {
                    var ruleId = propName is "eval" or "Function"
                        ? "RTSL01-DYNAMIC-CODE"
                        : propName is "Proxy" or "Reflect"
                            ? "RTSL01-METAPROG"
                            : "RTSL01-GLOBALS";

                    Report(
                        ruleId,
                        ScriptValidationSeverity.Error,
                        $"Access to 'globalThis.{propName}' is prohibited in Power Pages Server Logic.",
                        member);
                }
            }

            Visit(member.Object, false);
            if (member.Computed)
            {
                Visit(member.Property, false);
            }
        }

        private void CheckStandaloneIdentifier(Identifier id)
        {
            if (IsShadowed(id.Name))
            {
                return;
            }

            if (ProhibitedGlobals.Contains(id.Name))
            {
                Report(
                    "RTSL01-GLOBALS",
                    ScriptValidationSeverity.Error,
                    $"Global '{id.Name}' is not available in Power Pages Server Logic.",
                    id);
            }
            else if (MetaprogGlobalObjects.Contains(id.Name))
            {
                Report(
                    "RTSL01-METAPROG",
                    ScriptValidationSeverity.Error,
                    $"Metaprogramming global '{id.Name}' is prohibited in Power Pages Server Logic.",
                    id);
            }
        }

        private bool IsDataverseConnectorCall(Node expression)
        {
            var node = UnwrapChain(expression);
            if (node is not CallExpression call)
            {
                return false;
            }

            var callee = UnwrapChain(call.Callee);
            if (callee is not MemberExpression methodMember)
            {
                return false;
            }

            if (methodMember.Object is not MemberExpression dataverseMember ||
                !TryGetStaticPropertyName(dataverseMember, out var dataverseName) ||
                dataverseName != "Dataverse")
            {
                return false;
            }

            if (dataverseMember.Object is not MemberExpression connectorMember ||
                !TryGetStaticPropertyName(connectorMember, out var connectorName) ||
                connectorName != "Connector")
            {
                return false;
            }

            return connectorMember.Object is Identifier serverId &&
                   serverId.Name == "Server" &&
                   !IsShadowed("Server");
        }

        private static Node UnwrapChain(Node node)
        {
            while (node is ChainExpression chain)
            {
                node = chain.Expression;
            }

            return node;
        }

        private static bool TryGetStaticPropertyName(MemberExpression member, out string name)
        {
            if (!member.Computed && member.Property is Identifier id)
            {
                name = id.Name;
                return true;
            }

            if (member.Computed && member.Property is StringLiteral str)
            {
                name = str.Value;
                return true;
            }

            name = string.Empty;
            return false;
        }

        private void CollectHoistedDeclarations(NodeList<Statement> statements, bool includeLexical)
        {
            foreach (var stmt in statements)
            {
                CollectHoistedFromStatement(stmt, includeLexical);
            }
        }

        private void CollectHoistedFromStatement(Node? node, bool includeLexical)
        {
            if (node is null)
            {
                return;
            }

            switch (node)
            {
                case FunctionDeclaration fnDecl:
                    if (fnDecl.Id is not null)
                    {
                        AddDeclaredName(fnDecl.Id.Name);
                    }
                    return;

                case FunctionExpression:
                case ArrowFunctionExpression:
                case ClassDeclaration when !includeLexical:
                    return;

                case ClassDeclaration classDecl when includeLexical:
                    if (classDecl.Id is not null)
                    {
                        AddDeclaredName(classDecl.Id.Name);
                    }
                    return;

                case VariableDeclaration varDecl:
                    if (varDecl.Kind == VariableDeclarationKind.Var || includeLexical)
                    {
                        AddPatternNames(varDecl);
                    }
                    return;

                case BlockStatement block:
                    foreach (var child in block.Body)
                    {
                        CollectHoistedFromStatement(child, includeLexical: false);
                    }
                    return;

                default:
                    foreach (var child in node.ChildNodes)
                    {
                        if (child is not FunctionExpression and not ArrowFunctionExpression and not FunctionDeclaration)
                        {
                            CollectHoistedFromStatement(child, includeLexical: false);
                        }
                    }
                    return;
            }
        }

        private void CollectLexicalDeclarations(NodeList<Statement> statements)
        {
            foreach (var stmt in statements)
            {
                if (stmt is VariableDeclaration varDecl &&
                    varDecl.Kind is not VariableDeclarationKind.Var)
                {
                    AddPatternNames(varDecl);
                }
                else if (stmt is ClassDeclaration classDecl && classDecl.Id is not null)
                {
                    AddDeclaredName(classDecl.Id.Name);
                }
                else if (stmt is FunctionDeclaration fnDecl && fnDecl.Id is not null)
                {
                    AddDeclaredName(fnDecl.Id.Name);
                }
            }
        }

        private void AddPatternNames(Node node)
        {
            switch (node)
            {
                case Identifier id:
                    AddDeclaredName(id.Name);
                    break;
                case VariableDeclaration vd:
                    foreach (var d in vd.Declarations)
                    {
                        AddPatternNames(d.Id);
                    }
                    break;
                case ObjectPattern op:
                    foreach (var prop in op.Properties)
                    {
                        if (prop is AssignmentProperty ap)
                        {
                            AddPatternNames(ap.Value);
                        }
                        else if (prop is RestElement rest)
                        {
                            AddPatternNames(rest.Argument);
                        }
                    }
                    break;
                case ArrayPattern ap:
                    foreach (var elem in ap.Elements)
                    {
                        if (elem is not null)
                        {
                            AddPatternNames(elem);
                        }
                    }
                    break;
                case AssignmentPattern assign:
                    AddPatternNames(assign.Left);
                    break;
                case RestElement rest:
                    AddPatternNames(rest.Argument);
                    break;
            }
        }

        private void PushScope() => _scopes.Push(new HashSet<string>(StringComparer.Ordinal));

        private void PopScope() => _scopes.Pop();

        private void AddDeclaredName(string name)
        {
            if (_scopes.Count > 0)
            {
                _scopes.Peek().Add(name);
            }
        }

        private bool IsShadowed(string name)
        {
            foreach (var scope in _scopes)
            {
                if (scope.Contains(name))
                {
                    return true;
                }
            }

            return false;
        }

        private void Report(string ruleId, ScriptValidationSeverity severity, string message, Node node)
        {
            var start = node.Location.Start;
            var line = Math.Max(1, start.Line);
            var column = start.Column + 1;
            _diagnostics.Add(new ScriptValidationDiagnostic(ruleId, severity, message, line, column));
        }
    }
}
