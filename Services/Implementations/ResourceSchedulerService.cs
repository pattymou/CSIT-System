using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Services.Implementations;

public sealed class ResourceSchedulerService : IResourceSchedulerService
{
    private readonly AppDbContext _db;
    private readonly IApparatusResourceCapabilityService _resourceCapabilities;
    private readonly IApparatusAvailabilityService _availability;
    private readonly IReservationPolicyService _policy;

    public ResourceSchedulerService(
        AppDbContext db,
        IApparatusResourceCapabilityService resourceCapabilities,
        IApparatusAvailabilityService availability,
        IReservationPolicyService policy)
    {
        _db = db;
        _resourceCapabilities = resourceCapabilities;
        _availability = availability;
        _policy = policy;
    }

    public async Task<ResourceAssignmentProposal> ProposeAsync(
        ClaimsPrincipal user,
        ResourceSchedulerProposalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateTimeRange(request.StartTime, request.EndTime);
        await _policy.ValidateInitialDurationAsync(request.StartTime, request.EndTime, cancellationToken);
        if (request.TestExecutionProfileId == Guid.Empty)
            throw new ArgumentException("TestExecutionProfileId is required.", nameof(request));

        var profile = await _db.TestExecutionProfiles.AsNoTracking()
            .Include(x => x.TestEnvironment)
            .Include(x => x.EquipmentGroup).ThenInclude(x => x.Requirements)
            .SingleOrDefaultAsync(x => x.Id == request.TestExecutionProfileId, cancellationToken)
            ?? throw new KeyNotFoundException($"Test execution profile {request.TestExecutionProfileId} was not found.");
        if (profile.Status != TestExecutionProfileStatus.Active)
            throw new InvalidOperationException("Test execution profile is not Active.");
        if (profile.TestEnvironment.Status != TestEnvironmentStatus.Active)
            throw new InvalidOperationException("Test environment is not Active.");
        if (profile.EquipmentGroup.Status != EquipmentGroupStatus.Active)
            throw new InvalidOperationException("Equipment group is not Active.");

        var requirementStates = new List<RequirementState>();
        foreach (var requirement in profile.EquipmentGroup.Requirements
                     .OrderByDescending(x => x.Required)
                     .ThenBy(x => x.ResourceType, StringComparer.Ordinal)
                     .ThenBy(x => x.CapabilityTag, StringComparer.Ordinal)
                     .ThenBy(x => x.Id))
        {
            var matchingIds = await _resourceCapabilities.GetMatchingApparatusIdsAsync(
                requirement.ResourceType,
                requirement.CapabilityTag,
                cancellationToken: cancellationToken);
            string? configurationFailure = null;
            if (!string.IsNullOrWhiteSpace(requirement.PreferredEquipmentId)
                && !matchingIds.Contains(requirement.PreferredEquipmentId))
            {
                configurationFailure =
                    $"Catalog configuration error: preferred equipment does not match {FormatRequirement(requirement)}.";
            }
            if (!requirement.AllowAlternative && string.IsNullOrWhiteSpace(requirement.PreferredEquipmentId))
            {
                configurationFailure =
                    $"Catalog configuration error: {FormatRequirement(requirement)} disallows alternatives but has no preferred equipment.";
            }

            var availableIds = await _availability.GetAvailableApparatusIdsAsync(
                matchingIds.ToArray(), request.StartTime, request.EndTime, null, cancellationToken);
            IEnumerable<string> candidates = availableIds;
            if (configurationFailure is not null)
            {
                candidates = [];
            }
            else if (!requirement.AllowAlternative)
            {
                candidates = availableIds.Contains(requirement.PreferredEquipmentId!)
                    ? [requirement.PreferredEquipmentId!]
                    : [];
            }

            requirementStates.Add(new RequirementState(
                requirement,
                candidates.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                configurationFailure));
        }

        var assignment = Solve(requirementStates);
        var selectedIds = assignment.Values.SelectMany(x => x).Distinct(StringComparer.Ordinal).ToArray();
        var apparatusById = await _db.Apparatuses.AsNoTracking()
            .Where(x => selectedIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, StringComparer.Ordinal, cancellationToken);

        var requirements = requirementStates.Select(state =>
        {
            var selected = assignment[state.Requirement.Id];
            var unresolved = state.Requirement.Quantity - selected.Count;
            var failure = state.ConfigurationFailure;
            string? note = null;
            if (failure is null && unresolved > 0)
            {
                var message = $"Only {selected.Count} of {state.Requirement.Quantity} matching apparatus are available.";
                if (state.Requirement.Required) failure = message;
                else note = message;
            }

            return new ResourceAssignmentRequirementDto
            {
                EquipmentGroupRequirementId = state.Requirement.Id,
                ResourceType = state.Requirement.ResourceType,
                CapabilityTag = state.Requirement.CapabilityTag,
                Quantity = state.Requirement.Quantity,
                Required = state.Requirement.Required,
                AllowAlternative = state.Requirement.AllowAlternative,
                PreferredEquipmentId = state.Requirement.PreferredEquipmentId,
                SelectedApparatus = selected.Select(id => MapApparatus(apparatusById[id])).ToList(),
                UnresolvedQuantity = unresolved,
                FailureReason = state.Requirement.Required ? failure : state.ConfigurationFailure,
                Note = state.Requirement.Required ? null : note
            };
        }).ToList();

        string? policyFailure = null;
        var initiallyFeasible = requirements.All(x => x.FailureReason is null)
            && requirements.Where(x => x.Required).All(x => x.UnresolvedQuantity == 0);
        if (initiallyFeasible)
        {
            var account = user.FindFirstValue("account") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(account)) throw new UnauthorizedAccessException("Authenticated account claim is missing.");
            var department = await _db.Users.AsNoTracking()
                .Where(x => x.Account == account.Trim().ToLowerInvariant())
                .Select(x => x.Department)
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException("Authenticated user profile was not found.");
            var quota = await _policy.CheckDepartmentQuotaAsync(
                department, request.StartTime, request.EndTime, selectedIds.Length, cancellationToken: cancellationToken);
            policyFailure = quota.Error;
        }

        return new ResourceAssignmentProposal
        {
            IsFeasible = initiallyFeasible && policyFailure is null,
            PolicyFailure = policyFailure,
            TestExecutionProfile = MapReference(profile.Id, profile.Code, profile.Name),
            TestEnvironment = MapReference(profile.TestEnvironment.Id, profile.TestEnvironment.Code, profile.TestEnvironment.Name),
            EquipmentGroup = MapReference(profile.EquipmentGroup.Id, profile.EquipmentGroup.Code, profile.EquipmentGroup.Name),
            StartTime = request.StartTime,
            EndTime = request.EndTime,
            Requirements = requirements
        };
    }

    private static Dictionary<Guid, List<string>> Solve(IReadOnlyList<RequirementState> requirements)
    {
        var slots = requirements
            .SelectMany(x => Enumerable.Range(0, x.Requirement.Quantity).Select(_ => new AssignmentSlot(x)))
            .ToList();
        var apparatusIds = requirements.SelectMany(x => x.CandidateIds)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        var apparatusIndex = apparatusIds.Select((id, index) => (id, index))
            .ToDictionary(x => x.id, x => x.index, StringComparer.Ordinal);

        var source = 0;
        var firstSlot = 1;
        var firstApparatus = firstSlot + slots.Count;
        var sink = firstApparatus + apparatusIds.Length;
        var flow = new MinCostFlow(sink + 1);
        var assignmentEdges = new List<(int SlotIndex, string ApparatusId, MinCostFlow.Edge Edge)>();
        var alternativePenalty = (long)Math.Max(1, slots.Count * Math.Max(1, apparatusIds.Length) + 1);
        var optionalPenalty = (long)Math.Max(1, slots.Count) * (alternativePenalty + apparatusIds.Length + 1) + 1;

        for (var slotIndex = 0; slotIndex < slots.Count; slotIndex++)
        {
            var slotNode = firstSlot + slotIndex;
            flow.AddEdge(source, slotNode, 1, 0);
            var state = slots[slotIndex].State;
            foreach (var apparatusId in state.CandidateIds)
            {
                var rank = apparatusIndex[apparatusId];
                var isPreferred = string.Equals(
                    state.Requirement.PreferredEquipmentId,
                    apparatusId,
                    StringComparison.Ordinal);
                var preferenceCost = state.Requirement.PreferredEquipmentId is not null && !isPreferred
                    ? alternativePenalty
                    : 0;
                var edge = flow.AddEdge(slotNode, firstApparatus + rank, 1, preferenceCost + rank);
                assignmentEdges.Add((slotIndex, apparatusId, edge));
            }
            if (!state.Requirement.Required)
                flow.AddEdge(slotNode, sink, 1, optionalPenalty);
        }
        for (var i = 0; i < apparatusIds.Length; i++)
            flow.AddEdge(firstApparatus + i, sink, 1, 0);

        flow.Run(source, sink, slots.Count);
        var result = requirements.ToDictionary(x => x.Requirement.Id, _ => new List<string>());
        foreach (var edge in assignmentEdges.Where(x => x.Edge.Capacity == 0))
            result[slots[edge.SlotIndex].State.Requirement.Id].Add(edge.ApparatusId);
        foreach (var selected in result.Values)
            selected.Sort(StringComparer.Ordinal);
        return result;
    }

    private static ResourceAssignmentApparatusDto MapApparatus(Apparatus x) => new()
    {
        ApparatusId = x.Id,
        Name = x.Name,
        ProductsId = x.ProductsId,
        Kind = x.Kind,
        Brand = x.Brand,
        Model = x.Model,
        Place = x.Place,
        Custodian = string.IsNullOrWhiteSpace(x.Custodian) ? null : x.Custodian
    };

    private static SchedulerCatalogReferenceDto MapReference(Guid id, string code, string name) =>
        new() { Id = id, Code = code, Name = name };

    private static void ValidateTimeRange(DateTime startTime, DateTime endTime)
    {
        if (startTime.Kind != DateTimeKind.Utc || endTime.Kind != DateTimeKind.Utc)
            throw new InvalidOperationException("StartTime and EndTime must be UTC values.");
        if (startTime >= endTime)
            throw new InvalidOperationException("StartTime must be earlier than EndTime.");
    }

    private static string FormatRequirement(EquipmentGroupRequirement requirement) =>
        string.IsNullOrWhiteSpace(requirement.CapabilityTag)
            ? requirement.ResourceType
            : $"{requirement.ResourceType} / {requirement.CapabilityTag}";

    private sealed record RequirementState(
        EquipmentGroupRequirement Requirement,
        string[] CandidateIds,
        string? ConfigurationFailure);

    private sealed record AssignmentSlot(RequirementState State);

    private sealed class MinCostFlow
    {
        private readonly List<Edge>[] _graph;

        public MinCostFlow(int nodeCount)
        {
            _graph = Enumerable.Range(0, nodeCount).Select(_ => new List<Edge>()).ToArray();
        }

        public Edge AddEdge(int from, int to, int capacity, long cost)
        {
            var forward = new Edge(to, _graph[to].Count, capacity, cost);
            var reverse = new Edge(from, _graph[from].Count, 0, -cost);
            _graph[from].Add(forward);
            _graph[to].Add(reverse);
            return forward;
        }

        public void Run(int source, int sink, int requestedFlow)
        {
            for (var sent = 0; sent < requestedFlow; sent++)
            {
                var distance = Enumerable.Repeat(long.MaxValue, _graph.Length).ToArray();
                var previousNode = Enumerable.Repeat(-1, _graph.Length).ToArray();
                var previousEdge = Enumerable.Repeat(-1, _graph.Length).ToArray();
                var inQueue = new bool[_graph.Length];
                var queue = new Queue<int>();
                distance[source] = 0;
                queue.Enqueue(source);
                inQueue[source] = true;

                while (queue.Count != 0)
                {
                    var node = queue.Dequeue();
                    inQueue[node] = false;
                    for (var edgeIndex = 0; edgeIndex < _graph[node].Count; edgeIndex++)
                    {
                        var edge = _graph[node][edgeIndex];
                        if (edge.Capacity == 0 || distance[node] == long.MaxValue) continue;
                        var nextDistance = distance[node] + edge.Cost;
                        if (nextDistance >= distance[edge.To]) continue;
                        distance[edge.To] = nextDistance;
                        previousNode[edge.To] = node;
                        previousEdge[edge.To] = edgeIndex;
                        if (!inQueue[edge.To])
                        {
                            queue.Enqueue(edge.To);
                            inQueue[edge.To] = true;
                        }
                    }
                }

                if (previousNode[sink] < 0) return;
                for (var node = sink; node != source; node = previousNode[node])
                {
                    var edge = _graph[previousNode[node]][previousEdge[node]];
                    edge.Capacity--;
                    _graph[node][edge.ReverseIndex].Capacity++;
                }
            }
        }

        public sealed class Edge
        {
            public Edge(int to, int reverseIndex, int capacity, long cost)
            {
                To = to;
                ReverseIndex = reverseIndex;
                Capacity = capacity;
                Cost = cost;
            }

            public int To { get; }
            public int ReverseIndex { get; }
            public int Capacity { get; set; }
            public long Cost { get; }
        }
    }
}
