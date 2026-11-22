using System.Security;
using Jint;
using Jint.Native;
using Jint.Runtime;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetServerLogic.Abstractions;
using NetServerLogic.Options;
using NetServerLogic.Validation;

namespace NetServerLogic.Runtime;

/// <summary>
/// Executes Power Pages Server Logic scripts inside an isolated per-request <see cref="Jint.Engine"/> sandbox.
/// </summary>
public sealed class ServerLogicExecutionEngine
{
    private static readonly (string HttpMethod, string JsFunction)[] VerbMappings =
    [
        ("GET", "get"),
        ("POST", "post"),
        ("PUT", "put"),
        ("PATCH", "patch"),
        ("DELETE", "del")
    ];

    private static readonly string[] ProhibitedGlobalNames =
    [
        "console",
        "fetch",
        "XMLHttpRequest",
        "setTimeout",
        "setInterval",
        "clearTimeout",
        "clearInterval",
        "require",
        "process",
        "window"
    ];

    private readonly IServerLogicScriptValidator _validator;
    private readonly ServerGlobalFactory _globalFactory;
    private readonly ServerLogicEmulatorOptions _options;
    private readonly ILogger<ServerLogicExecutionEngine> _logger;

    public ServerLogicExecutionEngine(
        IServerLogicScriptValidator validator,
        ServerGlobalFactory globalFactory,
        IOptions<ServerLogicEmulatorOptions> options,
        ILogger<ServerLogicExecutionEngine> logger)
    {
        _validator = validator;
        _globalFactory = globalFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ServerLogicExecutionResult> ExecuteAsync(
        string scriptName,
        string scriptPath,
        string scriptSource,
        string httpMethod,
        HttpRequest? request = null,
        string? requestBody = null,
        MockUserOptions? resolvedUser = null,
        CancellationToken cancellationToken = default)
    {
        var validation = _validator.Validate(scriptSource, scriptName);
        foreach (var warning in validation.Warnings)
        {
            _logger.LogWarning(
                "Server Logic pre-flight warning [{RuleId}] in {Script}.js ({Line}:{Column}): {Message}",
                warning.RuleId,
                scriptName,
                warning.Line,
                warning.Column,
                warning.Message);
        }

        if (!validation.IsValid(_options.StrictValidatorMode))
        {
            var firstFailure = validation.Diagnostics.FirstOrDefault(d =>
                                   d.Severity == ScriptValidationSeverity.Error)
                               ?? validation.Diagnostics[0];

            return new ServerLogicExecutionResult
            {
                StatusCode = StatusCodes.Status400BadRequest,
                ErrorCode = "RTSL01",
                ErrorMessage = $"RTSL01: Script validation failed ({firstFailure.RuleId} at {firstFailure.Line}:{firstFailure.Column}): {firstFailure.Message}",
                ValidationDiagnostics = validation.Diagnostics
            };
        }

        var timeoutSeconds = Math.Max(1, _options.TimeoutInSeconds);
        var timeoutSpan = TimeSpan.FromSeconds(timeoutSeconds);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeoutSpan);

        using var engine = new Engine(opts =>
        {
            opts.Strict();
            opts.CancellationToken(cts.Token);
            opts.TimeoutInterval(timeoutSpan);
            opts.LimitRecursion(_options.MaxRecursionDepth);
            opts.LimitMemory(_options.MaxMemoryBytes);
            opts.ExperimentalFeatures = ExperimentalFeature.TaskInterop;
            opts.Constraints.PromiseTimeout = timeoutSpan;
        });

        ScrubProhibitedGlobals(engine);

        var serverGlobal = _globalFactory.CreateServerGlobal(
            engine,
            scriptName,
            request,
            requestBody ?? string.Empty,
            resolvedUser);
        engine.SetValue("Server", serverGlobal);

        try
        {
            engine.Execute(scriptSource, scriptPath);

            var targetFunctionName = MapHttpMethodToFunction(httpMethod);
            if (targetFunctionName is null)
            {
                return BuildMethodNotAllowedResult(engine, scriptName, httpMethod);
            }

            var handlerFn = engine.GetValue(targetFunctionName);
            if (handlerFn.IsUndefined() || handlerFn.IsNull() || !handlerFn.IsCallable())
            {
                return BuildMethodNotAllowedResult(engine, scriptName, httpMethod);
            }

            var rawResult = engine.Invoke(handlerFn);
            var resolved = await rawResult.UnwrapIfPromiseAsync(cts.Token).ConfigureAwait(false);

            if (!resolved.IsString())
            {
                return new ServerLogicExecutionResult
                {
                    StatusCode = StatusCodes.Status400BadRequest,
                    ErrorCode = "PARITY_RETURN_TYPE",
                    ErrorMessage =
                        $"Parity violation: '{targetFunctionName}()' in '{scriptName}.js' returned {resolved.Type}, but Power Pages Server Logic requires returning a string (e.g., JSON.stringify(result))."
                };
            }

            return new ServerLogicExecutionResult
            {
                StatusCode = StatusCodes.Status200OK,
                Output = resolved.AsString()
            };
        }
        catch (ExecutionCanceledException ex)
        {
            _logger.LogWarning(ex, "Server Logic script '{ScriptName}' exceeded timeout or was canceled.", scriptName);
            return new ServerLogicExecutionResult
            {
                StatusCode = StatusCodes.Status408RequestTimeout,
                ErrorCode = "SCRIPT_TIMEOUT",
                ErrorMessage = $"Server Logic script '{scriptName}' execution timed out or was canceled."
            };
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "Server Logic script '{ScriptName}' was canceled or timed out.", scriptName);
            return new ServerLogicExecutionResult
            {
                StatusCode = StatusCodes.Status408RequestTimeout,
                ErrorCode = "SCRIPT_TIMEOUT",
                ErrorMessage = $"Server Logic script '{scriptName}' execution timed out or was canceled."
            };
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "Server Logic script '{ScriptName}' timed out.", scriptName);
            return new ServerLogicExecutionResult
            {
                StatusCode = StatusCodes.Status408RequestTimeout,
                ErrorCode = "SCRIPT_TIMEOUT",
                ErrorMessage = $"Server Logic script '{scriptName}' execution timed out."
            };
        }
        catch (PromiseRejectedException ex)
        {
            _logger.LogError(ex, "Unhandled promise rejection in Server Logic script '{ScriptName}'.", scriptName);
            return new ServerLogicExecutionResult
            {
                StatusCode = StatusCodes.Status500InternalServerError,
                ErrorCode = "PROMISE_REJECTED",
                ErrorMessage = $"Unhandled promise rejection in '{scriptName}.js': {ex.Message}"
            };
        }
        catch (JavaScriptException ex)
        {
            _logger.LogError(ex, "JavaScript exception in Server Logic script '{ScriptName}'.", scriptName);
            return new ServerLogicExecutionResult
            {
                StatusCode = StatusCodes.Status500InternalServerError,
                ErrorCode = "JS_RUNTIME_ERROR",
                ErrorMessage = $"JavaScript error in '{scriptName}.js': {ex.Message}"
            };
        }
        catch (MemoryLimitExceededException ex)
        {
            _logger.LogError(ex, "Memory limit exceeded in Server Logic script '{ScriptName}'.", scriptName);
            return new ServerLogicExecutionResult
            {
                StatusCode = StatusCodes.Status500InternalServerError,
                ErrorCode = "MEMORY_LIMIT_EXCEEDED",
                ErrorMessage = $"Memory limit exceeded in '{scriptName}.js': {ex.Message}"
            };
        }
        catch (RecursionDepthOverflowException ex)
        {
            _logger.LogError(ex, "Recursion depth exceeded in Server Logic script '{ScriptName}'.", scriptName);
            return new ServerLogicExecutionResult
            {
                StatusCode = StatusCodes.Status500InternalServerError,
                ErrorCode = "RECURSION_LIMIT_EXCEEDED",
                ErrorMessage = $"Recursion depth limit exceeded in '{scriptName}.js': {ex.Message}"
            };
        }
        catch (SecurityException ex)
        {
            _logger.LogWarning(ex, "Security policy violation in Server Logic script '{ScriptName}'.", scriptName);
            return new ServerLogicExecutionResult
            {
                StatusCode = StatusCodes.Status500InternalServerError,
                ErrorCode = "SECURITY_ERROR",
                ErrorMessage = ex.Message
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error executing Server Logic script '{ScriptName}'.", scriptName);
            return new ServerLogicExecutionResult
            {
                StatusCode = StatusCodes.Status500InternalServerError,
                ErrorCode = "EXECUTION_ERROR",
                ErrorMessage = ex.Message
            };
        }
    }

    private static void ScrubProhibitedGlobals(Engine engine)
    {
        foreach (var globalName in ProhibitedGlobalNames)
        {
            if (!engine.Global.Delete(globalName))
            {
                engine.SetValue(globalName, JsValue.Undefined);
            }
        }
    }

    private static string? MapHttpMethodToFunction(string httpMethod)
    {
        foreach (var mapping in VerbMappings)
        {
            if (mapping.HttpMethod.Equals(httpMethod, StringComparison.OrdinalIgnoreCase))
            {
                return mapping.JsFunction;
            }
        }

        return null;
    }

    private static ServerLogicExecutionResult BuildMethodNotAllowedResult(
        Engine engine,
        string scriptName,
        string httpMethod)
    {
        var allowed = new List<string>();
        foreach (var mapping in VerbMappings)
        {
            var candidate = engine.GetValue(mapping.JsFunction);
            if (!candidate.IsUndefined() && !candidate.IsNull() && candidate.IsCallable())
            {
                allowed.Add(mapping.HttpMethod);
            }
        }

        return new ServerLogicExecutionResult
        {
            StatusCode = StatusCodes.Status405MethodNotAllowed,
            ErrorCode = "METHOD_NOT_ALLOWED",
            ErrorMessage = $"HTTP method '{httpMethod.ToUpperInvariant()}' is not implemented in '{scriptName}.js'.",
            AllowedMethods = allowed
        };
    }
}
