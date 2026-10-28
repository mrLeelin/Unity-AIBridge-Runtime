namespace AIBridge.Runtime
{
    /// <summary>
    /// Runtime command handler interface.
    /// Implement this interface to extend AIBridge runtime functionality.
    ///
    /// Example usage:
    /// <code>
    /// public class MyUIHandler : IAIBridgeHandler
    /// {
    ///     public string[] SupportedActions => new[] { "trigger_event", "get_panels" };
    ///
    ///     public AIBridgeRuntimeCommandResult HandleCommand(AIBridgeRuntimeCommand command)
    ///     {
    ///         switch (command.Action)
    ///         {
    ///             case "trigger_event":
    ///                 // Trigger UI event...
    ///                 return AIBridgeRuntimeCommandResult.FromSuccess(command.Id, new { triggered = true });
    ///             case "get_panels":
    ///                 // Return open panels...
    ///                 return AIBridgeRuntimeCommandResult.FromSuccess(command.Id, new { panels = panelList });
    ///             default:
    ///                 return null; // Not handled
    ///         }
    ///     }
    /// }
    /// </code>
    /// </summary>
    public interface IAIBridgeHandler
    {
        /// <summary>
        /// List of action types this handler supports.
        /// AIBridge will only call HandleCommand for matching actions.
        /// </summary>
        string[] SupportedActions { get; }

        /// <summary>
        /// Handle a runtime command.
        /// </summary>
        /// <param name="command">The command to handle</param>
        /// <returns>Result if handled, null if not handled (will try next handler)</returns>
        AIBridgeRuntimeCommandResult HandleCommand(AIBridgeRuntimeCommand command);
    }

    /// <summary>
    /// Async runtime command handler interface.
    /// Use this for handlers that need to perform async operations.
    /// </summary>
    public interface IAIBridgeAsyncHandler
    {
        /// <summary>
        /// List of action types this handler supports.
        /// </summary>
        string[] SupportedActions { get; }

        /// <summary>
        /// Handle a runtime command asynchronously.
        /// </summary>
        /// <param name="command">The command to handle</param>
        /// <param name="callback">Callback to invoke with the result</param>
        /// <returns>True if the command is being handled, false to try next handler</returns>
        bool HandleCommandAsync(AIBridgeRuntimeCommand command, System.Action<AIBridgeRuntimeCommandResult> callback);
    }
}
