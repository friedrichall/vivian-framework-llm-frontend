#if UNITY_EDITOR
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Vivian.Editor.Models
{
    public sealed class InteractionPlanData
    {
        [JsonProperty("element_roles")]
        public List<ElementRoleData> ElementRoles { get; set; } = new List<ElementRoleData>();

        [JsonProperty("planned_states")]
        public List<PlannedStateData> PlannedStates { get; set; } = new List<PlannedStateData>();

        [JsonProperty("planned_transitions")]
        public List<PlannedTransitionData> PlannedTransitions { get; set; } = new List<PlannedTransitionData>();

        [JsonProperty("files_needed")]
        public List<string> FilesNeeded { get; set; } = new List<string>();

        [JsonProperty("reasoning")]
        public string Reasoning { get; set; } = string.Empty;
    }

    public sealed class ElementRoleData
    {
        [JsonProperty("object_name")]
        public string ObjectName { get; set; } = string.Empty;

        [JsonProperty("funcspec_type")]
        public string FuncSpecType { get; set; } = string.Empty;

        [JsonProperty("category")]
        public string Category { get; set; } = string.Empty;

        [JsonProperty("rationale")]
        public string Rationale { get; set; } = string.Empty;
    }

    public sealed class PlannedStateData
    {
        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("description")]
        public string Description { get; set; } = string.Empty;

        [JsonProperty("involved_elements")]
        public List<string> InvolvedElements { get; set; } = new List<string>();

        [JsonProperty("screen_files")]
        public List<string> ScreenFiles { get; set; }
    }

    public sealed class PlannedTransitionData
    {
        [JsonProperty("source_state")]
        public string SourceState { get; set; } = string.Empty;

        [JsonProperty("destination_state")]
        public string DestinationState { get; set; } = string.Empty;

        [JsonProperty("trigger_element")]
        public string TriggerElement { get; set; }

        [JsonProperty("trigger_description")]
        public string TriggerDescription { get; set; } = string.Empty;

        [JsonProperty("guard_hints")]
        public List<string> GuardHints { get; set; }
    }
}
#endif
