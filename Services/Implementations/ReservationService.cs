using System.Data;
using System.Linq.Expressions;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SIT.DepartmentSystem.Web.Data;
using SIT.DepartmentSystem.Web.Entities;
using SIT.DepartmentSystem.Web.Models;
using SIT.DepartmentSystem.Web.Models.Api;
using SIT.DepartmentSystem.Web.Services.Interfaces;

namespace SIT.DepartmentSystem.Web.Services.Implementations;

public sealed class ReservationService : IReservationService
{
    private const string DirectSingleApparatusError = "Direct 預約一次只能包含 1 台設備。";
    private readonly AppDbContext _db;
    private readonly IApparatusAvailabilityService _availability;
    private readonly IApparatusResourceCapabilityService _resourceCapabilities;
    private readonly IReservationPolicyService _policy;
    private readonly IEnvironmentGroupDeviceService _environmentGroupDevices;

    public ReservationService(
        AppDbContext db,
        IApparatusAvailabilityService availability,
        IApparatusResourceCapabilityService resourceCapabilities,
        IReservationPolicyService policy,
        IEnvironmentGroupDeviceService environmentGroupDevices)
    {
        _db = db;
        _availability = availability;
        _resourceCapabilities = resourceCapabilities;
        _policy = policy;
        _environmentGroupDevices = environmentGroupDevices;
    }

    public async Task<IReadOnlyList<ReservationEnvironmentOptionDto>> GetEnvironmentOptionsAsync(
        CancellationToken cancellationToken = default) =>
        await _db.TestEnvironments.AsNoTracking()
            .Where(x => x.Status == TestEnvironmentStatus.Active)
            .Where(x => x.ExecutionProfiles.Any(p =>
                p.Status == TestExecutionProfileStatus.Active
                && p.EquipmentGroup.Status == EquipmentGroupStatus.Active))
            .OrderBy(x => x.Name)
            .Select(x => new ReservationEnvironmentOptionDto
            {
                Id = x.Id,
                Code = x.Code,
                Name = x.Name,
                Profiles = x.ExecutionProfiles
                    .Where(p => p.Status == TestExecutionProfileStatus.Active
                        && p.EquipmentGroup.Status == EquipmentGroupStatus.Active)
                    .OrderBy(p => p.Name)
                    .Select(p => new ReservationProfileOptionDto
                    {
                        Id = p.Id,
                        Code = p.Code,
                        Name = p.Name,
                        TestCapabilityId = p.TestCapabilityId,
                        TestCapabilityCode = p.TestCapability.Code,
                        TestCapabilityName = p.TestCapability.Name,
                        EquipmentGroupId = p.EquipmentGroupId,
                        EquipmentGroupCode = p.EquipmentGroup.Code,
                        EquipmentGroupName = p.EquipmentGroup.Name,
                        Requirements = p.EquipmentGroup.Requirements
                            .OrderBy(r => r.ResourceType).ThenBy(r => r.Id)
                            .Select(r => new ReservationRequirementOptionDto
                            {
                                Id = r.Id,
                                ResourceType = r.ResourceType,
                                CapabilityTag = r.CapabilityTag,
                                Quantity = r.Quantity,
                                Required = r.Required,
                                AllowAlternative = r.AllowAlternative,
                                PreferredEquipmentId = r.PreferredEquipmentId,
                                PreferredEquipmentName = r.PreferredEquipment == null ? null : r.PreferredEquipment.Name,
                                PreferredEquipmentBookable = r.PreferredEquipment != null
                                    && r.PreferredEquipment.ModuleCode == ApparatusReservationRules.EquipmentModuleCode
                                    && r.PreferredEquipment.ReservationStatus == ApparatusReservationRules.BookableStatus
                            }).ToList()
                    }).ToList()
             }).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ReservationEnvironmentGroupTeamDto>> GetEnvironmentGroupTeamsAsync(
        CancellationToken cancellationToken = default) =>
        await _db.SystemOptions.AsNoTracking()
            .Where(x => x.Category == SystemOptionCategories.Team
                && x.IsEnabled
                && _db.EquipmentGroups.Any(group =>
                    group.OwnerTeamOptionId == x.Id
                    && group.Status == EquipmentGroupStatus.Active))
            .OrderBy(x => x.Sort).ThenBy(x => x.Name).ThenBy(x => x.Value)
            .Select(x => new ReservationEnvironmentGroupTeamDto
            {
                TeamOptionId = x.Id,
                DisplayName = x.Name,
                Value = x.Value,
                EnvironmentGroupCount = _db.EquipmentGroups.Count(group =>
                    group.OwnerTeamOptionId == x.Id
                    && group.Status == EquipmentGroupStatus.Active)
            })
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ReservationEnvironmentGroupDto>> GetEnvironmentGroupsAsync(
        Guid? teamOptionId = null,
        CancellationToken cancellationToken = default)
    {
        var query = _db.EquipmentGroups.AsNoTracking()
            .Include(x => x.OwnerTeamOption)
            .Include(x => x.Devices).ThenInclude(x => x.Apparatus)
            .Where(x => x.Status == EquipmentGroupStatus.Active);
        if (teamOptionId.HasValue)
            query = query.Where(x => x.OwnerTeamOptionId == teamOptionId.Value);

        var groups = await query
            .OrderBy(x => x.Name).ThenBy(x => x.Code)
            .ToListAsync(cancellationToken);
        return groups.Select(MapEnvironmentGroup).ToList();
    }

    public async Task<ReservationEnvironmentGroupDto?> GetEnvironmentGroupAsync(
        Guid groupId,
        CancellationToken cancellationToken = default)
    {
        var group = await _db.EquipmentGroups.AsNoTracking()
            .Include(x => x.OwnerTeamOption)
            .Include(x => x.Devices).ThenInclude(x => x.Apparatus)
            .SingleOrDefaultAsync(
                x => x.Id == groupId && x.Status == EquipmentGroupStatus.Active,
                cancellationToken);
        return group is null ? null : MapEnvironmentGroup(group);
    }

    public async Task<IReadOnlyList<ReservationOverviewDto>> GetEnvironmentGroupCalendarAsync(
        Guid groupId,
        DateTime start,
        DateTime end,
        bool includeHistory = false,
        CancellationToken cancellationToken = default)
    {
        ValidateTimeRange(start, end);
        if (end - start > TimeSpan.FromDays(93))
            throw new InvalidOperationException("Calendar range cannot exceed 93 days.");
        if (!await _db.EquipmentGroups.AsNoTracking().AnyAsync(
                x => x.Id == groupId && x.Status == EquipmentGroupStatus.Active,
                cancellationToken))
            throw new KeyNotFoundException($"Equipment group {groupId} was not found.");

        var memberships = _db.EquipmentGroupDevices.AsNoTracking()
            .Where(x => x.EquipmentGroupId == groupId);
        var statuses = includeHistory
            ? Enum.GetValues<ReservationStatus>()
            : ReservationOccupancyRules.BlockingStatuses;
        var now = DateTime.UtcNow;

        // Query from Reservation rather than joining into the result set. A reservation
        // that contains several members of the group therefore remains one calendar event.
        return await _db.Reservations.AsNoTracking()
            .Where(x => x.StartTime < end && start < x.EndTime && statuses.Contains(x.Status))
            .Where(x => x.Items.Any(item => memberships.Any(member => member.ApparatusId == item.ApparatusId)))
            .OrderBy(x => x.StartTime).ThenBy(x => x.ReservationNo)
            .Select(x => new ReservationOverviewDto
            {
                ReservationId = x.Id,
                ReservationNo = x.ReservationNo,
                StartTime = x.StartTime,
                EndTime = x.EndTime,
                Status = x.Status,
                ApplicantAccount = x.ApplicantAccount,
                ApplicantDepartment = x.ApplicantDepartment,
                ApplicantName = x.ApplicantName,
                ApplicantExtension = x.ApplicantExtension,
                Purpose = x.Purpose,
                IsOverdue = (x.Status == ReservationStatus.Approved || x.Status == ReservationStatus.Borrowed)
                    && x.EndTime < now,
                Mode = x.EquipmentGroupId.HasValue || x.TestExecutionProfileId.HasValue
                    ? ReservationMode.Environment
                    : ReservationMode.Direct,
                TestEnvironmentName = x.TestEnvironmentNameSnapshot,
                EquipmentGroupName = x.EquipmentGroupNameSnapshot,
                TestExecutionProfileName = x.TestExecutionProfileNameSnapshot,
                CreatedAt = x.CreatedAt,
                RelatedDeviceCount = x.Items.Count(item =>
                    memberships.Any(member => member.ApparatusId == item.ApparatusId)),
                Apparatus = x.Items
                    .Where(item => memberships.Any(member => member.ApparatusId == item.ApparatusId))
                    .OrderBy(item => item.ApparatusId)
                    .Select(item => new ReservationOverviewApparatusDto
                    {
                        Id = item.ApparatusId,
                        Name = item.ApparatusName,
                        ProductsId = item.ProductsId,
                        Kind = item.Kind,
                        Brand = item.Brand,
                        Model = item.Model
                    }).ToList()
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<ReservationApplicationOptionsDto> GetApplicationOptionsAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        EnsureReservationUser(user);
        var applicant = await ResolveApplicantAsync(user, cancellationToken);
        var options = await _db.SystemOptions.AsNoTracking()
            .Where(x => x.IsEnabled
                && (x.Category == SystemOptionCategories.Department
                    || x.Category == SystemOptionCategories.Customer
                    || x.Category == SystemOptionCategories.SubPu))
            .OrderBy(x => x.Sort).ThenBy(x => x.Name).ThenBy(x => x.Value)
            .Select(x => new { x.Category, x.Name, x.Value })
            .ToListAsync(cancellationToken);

        return new ReservationApplicationOptionsDto
        {
            Applicant = new ReservationApplicantSnapshotDto
            {
                Name = Require(applicant.DisplayName, nameof(AppUser.DisplayName)),
                Department = Require(applicant.Department, nameof(AppUser.Department)),
                Email = Clean(applicant.Email)
            },
            Departments = options.Where(x => x.Category == SystemOptionCategories.Department)
                .Select(x => ToReservationOption(x.Value, x.Name)).ToList(),
            Customers = options.Where(x => x.Category == SystemOptionCategories.Customer)
                .Select(x => ToReservationOption(x.Value, x.Name)).ToList(),
            SubPus = options.Where(x => x.Category == SystemOptionCategories.SubPu)
                .Select(x => ToReservationOption(x.Value, x.Name)).ToList()
        };
    }

    public async Task<ReservationDetailDto> CreateAsync(
        ClaimsPrincipal user,
        CreateReservationRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureReservationUser(user);
        ArgumentNullException.ThrowIfNull(request);
        ValidateTimeRange(request.StartTime, request.EndTime);
        await _policy.ValidateInitialDurationAsync(request.StartTime, request.EndTime, cancellationToken);

        var applicant = await ResolveApplicantAsync(user, cancellationToken);
        var purpose = Require(request.Purpose, nameof(request.Purpose));
        var extension = Require(request.ApplicantExtension, nameof(request.ApplicantExtension));

        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            var prepared = await PrepareRequestAsync(
                request.Mode, request.Items, request.EnvironmentGroupId,
                request.TestExecutionProfileId, request.Selections, cancellationToken);
            if (request.Mode == ReservationMode.Environment)
                await EnsureEnvironmentNoOverlapAsync(prepared.ApparatusIds, request.StartTime, request.EndTime, null, cancellationToken);
            else
                await _availability.EnsureNoOverlapAsync(prepared.ApparatusIds, request.StartTime, request.EndTime, null, cancellationToken);
            await _policy.EnsureDepartmentQuotaAsync(
                applicant.Department, request.StartTime, request.EndTime, prepared.Items.Count,
                acquireTransactionLock: true, cancellationToken: cancellationToken);

            var now = DateTime.UtcNow;
            var sequence = await _db.Database
                .SqlQueryRaw<long>("SELECT nextval('reservation_no_seq') AS \"Value\"")
                .SingleAsync(cancellationToken);

            var entity = new Reservation
            {
                Id = Guid.NewGuid(),
                ReservationNo = $"RSV-{now:yyyyMMdd}-{sequence:D6}",
                ApplicantAccount = Require(applicant.Account, nameof(AppUser.Account)).ToLowerInvariant(),
                ApplicantName = Require(applicant.DisplayName, nameof(AppUser.DisplayName)),
                ApplicantDepartment = Require(applicant.Department, nameof(AppUser.Department)),
                ApplicantEmail = Clean(applicant.Email),
                ApplicantExtension = extension,
                Purpose = purpose,
                ProductModelName = Clean(request.ProductModelName),
                Customer = Clean(request.Customer),
                ProjectSubPu = Clean(request.ProjectSubPu),
                Note = Clean(request.Note),
                ApplicantAgentName = Clean(request.ApplicantAgentName),
                ApplicantAgentExtension = Clean(request.ApplicantAgentExtension),
                ApplicantAgentEmail = Clean(request.ApplicantAgentEmail),
                StartTime = request.StartTime,
                EndTime = request.EndTime,
                CreatedAt = now,
                UpdatedAt = now,
                Items = prepared.Items
            };
            entity.ApplyEnvironmentContext(prepared.EnvironmentContext);
            AddAudit(entity, NewAudit(entity.Id, ReservationAuditActions.Created, null, ReservationStatus.Draft,
                entity.ApplicantAccount, entity.ApplicantName, now));

            _db.Reservations.Add(entity);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return MapDetail(entity);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<ReservationDetailDto?> GetByIdAsync(
        Guid id,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var scope = GetKnownScope(user);
        var account = GetAccount(user);
        var entity = await _db.Reservations.AsNoTracking()
            .Include(x => x.Items)
            .Include(x => x.ExtensionRequests)
            .Include(x => x.AuditEvents)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null) return null;

        if (scope == SystemAuthorization.AccessScopes.RdApplicant && entity.ApplicantAccount != account)
            throw new UnauthorizedAccessException("The reservation does not belong to the authenticated applicant.");

        return MapDetail(entity);
    }

    public async Task<ReservationDetailDto> UpdateAsync(
        Guid id,
        ClaimsPrincipal user,
        UpdateReservationRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureReservationUser(user);
        ArgumentNullException.ThrowIfNull(request);
        ValidateTimeRange(request.StartTime, request.EndTime);
        await _policy.ValidateInitialDurationAsync(request.StartTime, request.EndTime, cancellationToken);
        var purpose = Require(request.Purpose, nameof(request.Purpose));
        var extension = Require(request.ApplicantExtension, nameof(request.ApplicantExtension));
        var account = GetAccount(user);

        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            var entity = await FindRequiredForUpdateAsync(id, cancellationToken);
            EnsureOwner(entity, account);
            if (entity.Status != ReservationStatus.Draft)
                throw new InvalidOperationException("Only a Draft reservation can be updated.");
            var existingMode = IsEnvironmentReservation(entity) ? ReservationMode.Environment : ReservationMode.Direct;
            if (request.Mode != existingMode)
                throw new InvalidOperationException("A Draft reservation cannot change between Direct and Environment mode.");

            var prepared = await PrepareRequestAsync(
                request.Mode, request.Items, request.EnvironmentGroupId,
                request.TestExecutionProfileId, request.Selections, cancellationToken);
            if (request.Mode == ReservationMode.Environment)
                await EnsureEnvironmentNoOverlapAsync(prepared.ApparatusIds, request.StartTime, request.EndTime, entity.Id, cancellationToken);
            else
                await _availability.EnsureNoOverlapAsync(prepared.ApparatusIds, request.StartTime, request.EndTime, entity.Id, cancellationToken);
            await _policy.EnsureDepartmentQuotaAsync(
                entity.ApplicantDepartment, request.StartTime, request.EndTime, prepared.Items.Count, entity.Id,
                acquireTransactionLock: true, cancellationToken: cancellationToken);

            var existingItems = entity.Items.ToList();
            await _db.ReservationItems
                .Where(x => x.ReservationId == entity.Id)
                .ExecuteDeleteAsync(cancellationToken);
            foreach (var existingItem in existingItems)
                _db.Entry(existingItem).State = EntityState.Detached;
            entity.Items = [];

            entity.UpdateDraft(
                purpose,
                request.ProductModelName,
                request.Customer,
                request.ProjectSubPu,
                request.Note,
                request.StartTime,
                request.EndTime,
                extension,
                request.ApplicantAgentName,
                request.ApplicantAgentExtension,
                request.ApplicantAgentEmail,
                prepared.Items,
                prepared.EnvironmentContext,
                DateTime.UtcNow);
            _db.ReservationItems.AddRange(entity.Items);
            AddAudit(entity, NewAudit(entity.Id, ReservationAuditActions.Updated, ReservationStatus.Draft,
                ReservationStatus.Draft, account, entity.ApplicantName, entity.UpdatedAt));

            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return MapDetail(entity);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<IReadOnlyList<ReservationListDto>> GetListAsync(
        ClaimsPrincipal user,
        ReservationStatus? status = null,
        bool active = false,
        CancellationToken cancellationToken = default)
    {
        EnsureReservationUser(user);
        var account = GetAccount(user);
        var query = _db.Reservations.AsNoTracking().AsQueryable();
        query = query.Where(x => x.ApplicantAccount == account);
        if (active)
            query = query.Where(x => x.Status == ReservationStatus.Approved || x.Status == ReservationStatus.Borrowed);
        else if (status.HasValue)
            query = query.Where(x => x.Status == status.Value);

        return await MapList(query).ToListAsync(cancellationToken);
    }

    public async Task<ReservationReviewQueueDto> GetReviewQueueAsync(
        ClaimsPrincipal user,
        ReservationReviewScope scope,
        Guid? teamOptionId = null,
        bool includeHistory = false,
        CancellationToken cancellationToken = default)
    {
        var access = await ResolveReviewAccessAsync(user, cancellationToken);
        EnsureReviewScopeAllowed(access, scope);
        var teamScope = await ResolveTeamScopeAsync(
            access,
            scope == ReservationReviewScope.Team,
            scope == ReservationReviewScope.All,
            teamOptionId,
            cancellationToken);

        var query = _db.Reservations.AsNoTracking()
            .Include(x => x.Items).ThenInclude(x => x.Apparatus).ThenInclude(x => x.OwnerTeamOption)
            .Include(x => x.EquipmentGroup).ThenInclude(x => x!.OwnerTeamOption)
            .AsSplitQuery()
            .Where(x => x.Status != ReservationStatus.Draft);

        if (!includeHistory)
            query = query.Where(x => x.Status == ReservationStatus.Pending);

        query = scope switch
        {
            ReservationReviewScope.Custodian => query.Where(x =>
                !x.EquipmentGroupId.HasValue && !x.TestExecutionProfileId.HasValue
                && x.Items.Any(i => i.Apparatus.CustodianAccount != null
                    && i.Apparatus.CustodianAccount.ToLower() == access.Account)),
            ReservationReviewScope.Team or ReservationReviewScope.All =>
                query.Where(BuildReservationTeamFilter(teamScope.TeamOptionIds)),
            _ => throw new ArgumentException("Review scope is invalid.", nameof(scope))
        };

        var entities = await query.OrderBy(x => x.StartTime).ThenBy(x => x.ReservationNo)
            .ToListAsync(cancellationToken);
        var custodianProfiles = await ApparatusCustodianResolver.LoadDisplayNamesAsync(
            _db,
            entities.SelectMany(x => x.Items).Select(x => x.Apparatus.CustodianAccount),
            cancellationToken);
        return new ReservationReviewQueueDto
        {
            Scope = scope,
            SelectedTeamOptionId = teamScope.SelectedTeamOptionId,
            IncludeHistory = includeHistory,
            IsTeamLeader = access.LeaderTeamIds.Count != 0,
            IsAdmin = access.IsAdmin,
            TeamOptions = teamScope.Options,
            Entries = entities.Select(x => MapReviewEntry(x, access, scope, custodianProfiles)).ToList()
        };
    }

    public async Task<ReservationDetailDto?> GetReviewDetailAsync(
        Guid id,
        ClaimsPrincipal user,
        ReservationReviewScope scope,
        CancellationToken cancellationToken = default)
    {
        var access = await ResolveReviewAccessAsync(user, cancellationToken);
        EnsureReviewScopeAllowed(access, scope);
        var entity = await _db.Reservations.AsNoTracking()
            .Include(x => x.Items).ThenInclude(x => x.Apparatus).ThenInclude(x => x.OwnerTeamOption)
            .Include(x => x.EquipmentGroup).ThenInclude(x => x!.OwnerTeamOption)
            .Include(x => x.ExtensionRequests)
            .Include(x => x.AuditEvents)
            .AsSplitQuery()
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null) return null;
        if (!CanViewReview(entity, access, scope))
            throw new UnauthorizedAccessException("The reservation is outside the selected review responsibility.");
        var custodianProfiles = await ApparatusCustodianResolver.LoadDisplayNamesAsync(
            _db,
            entity.Items.Select(x => x.Apparatus.CustodianAccount),
            cancellationToken);
        return MapReviewDetail(entity, access, scope, custodianProfiles);
    }

    private static IQueryable<ReservationListDto> MapList(IQueryable<Reservation> query) => query
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new ReservationListDto
            {
                Id = x.Id,
                ReservationNo = x.ReservationNo,
                ApplicantAccount = x.ApplicantAccount,
                ApplicantName = x.ApplicantName,
                ApplicantDepartment = x.ApplicantDepartment,
                ApplicantExtension = x.ApplicantExtension,
                Purpose = x.Purpose,
                StartTime = x.StartTime,
                EndTime = x.EndTime,
                Status = x.Status,
                Mode = x.EquipmentGroupId.HasValue || x.TestExecutionProfileId.HasValue
                    ? ReservationMode.Environment
                    : ReservationMode.Direct,
                ItemCount = x.Items.Count,
                ApparatusNames = x.Items.OrderBy(i => i.ApparatusId).Select(i => i.ApparatusName).ToList(),
                CreatedAt = x.CreatedAt,
                UpdatedAt = x.UpdatedAt
                ,BorrowedAt = x.BorrowedAt
            });

    public async Task<ReservationOverviewPageDto> GetOverviewAsync(
        ReservationOverviewQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateTimeRange(query.From, query.To);
        if (query.To - query.From > TimeSpan.FromDays(93))
            throw new InvalidOperationException("Overview range cannot exceed 93 days.");
        if (query.Page < 1) throw new InvalidOperationException("Page must be at least 1.");
        if (query.PageSize is < 1 or > 200) throw new InvalidOperationException("PageSize must be between 1 and 200.");

        var statuses = query.IncludeHistory
            ? Enum.GetValues<ReservationStatus>()
            : [ReservationStatus.Pending, ReservationStatus.Approved, ReservationStatus.Borrowed];
        var source = _db.Reservations.AsNoTracking()
            .Where(x => x.StartTime < query.To && query.From < x.EndTime && statuses.Contains(x.Status));
        if (query.Status.HasValue) source = source.Where(x => x.Status == query.Status.Value);
        if (!string.IsNullOrWhiteSpace(query.ApparatusId))
        {
            var apparatusId = query.ApparatusId.Trim();
            source = source.Where(x => x.Items.Any(i => i.ApparatusId == apparatusId));
        }
        if (!string.IsNullOrWhiteSpace(query.Department))
        {
            var department = query.Department.Trim();
            source = source.Where(x => x.ApplicantDepartment == department);
        }
        if (!string.IsNullOrWhiteSpace(query.Borrower))
        {
            var borrower = query.Borrower.Trim().ToLower();
            source = source.Where(x => x.ApplicantName.ToLower().Contains(borrower)
                || x.ApplicantAccount.ToLower().Contains(borrower)
                || (x.ApplicantExtension != null && x.ApplicantExtension.ToLower().Contains(borrower)));
        }

        var now = DateTime.UtcNow;
        var totalCount = await source.CountAsync(cancellationToken);
        var items = await source.OrderBy(x => x.StartTime).ThenBy(x => x.ReservationNo)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(x => new ReservationOverviewDto
            {
                ReservationId = x.Id,
                ReservationNo = x.ReservationNo,
                StartTime = x.StartTime,
                EndTime = x.EndTime,
                Status = x.Status,
                ApplicantAccount = x.ApplicantAccount,
                ApplicantDepartment = x.ApplicantDepartment,
                ApplicantName = x.ApplicantName,
                ApplicantExtension = x.ApplicantExtension,
                Purpose = x.Purpose,
                IsOverdue = (x.Status == ReservationStatus.Approved || x.Status == ReservationStatus.Borrowed)
                    && x.EndTime < now,
                Mode = x.EquipmentGroupId.HasValue || x.TestExecutionProfileId.HasValue
                    ? ReservationMode.Environment
                    : ReservationMode.Direct,
                TestEnvironmentName = x.TestEnvironmentNameSnapshot,
                EquipmentGroupName = x.EquipmentGroupNameSnapshot,
                TestExecutionProfileName = x.TestExecutionProfileNameSnapshot,
                CreatedAt = x.CreatedAt,
                Apparatus = x.Items.OrderBy(i => i.ApparatusId).Select(i => new ReservationOverviewApparatusDto
                {
                    Id = i.ApparatusId,
                    Name = i.ApparatusName,
                    ProductsId = i.ProductsId,
                    Kind = i.Kind,
                    Brand = i.Brand,
                    Model = i.Model
                }).ToList()
            }).ToListAsync(cancellationToken);

        return new ReservationOverviewPageDto
        {
            TotalCount = totalCount,
            Page = query.Page,
            PageSize = query.PageSize,
            Items = items
        };
    }

    public Task<ReservationPolicySettings> GetPolicySettingsAsync(CancellationToken cancellationToken = default) =>
        _policy.GetSettingsAsync(cancellationToken);

    public async Task<ReservationExtensionRequestDto> RequestExtensionAsync(
        Guid id,
        ClaimsPrincipal user,
        ReservationExtensionCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureReservationUser(user);
        ArgumentNullException.ThrowIfNull(request);
        var account = GetAccount(user);
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            var entity = await FindRequiredForUpdateAsync(id, cancellationToken);
            EnsureOwner(entity, account);
            if (entity.Status is not ReservationStatus.Approved and not ReservationStatus.Borrowed)
                throw new InvalidOperationException("Only an Approved or Borrowed reservation can request an extension.");
            if (entity.ExtensionRequests.Any(x => x.Status == ReservationExtensionRequestStatus.Pending))
                throw new InvalidOperationException("This reservation already has a Pending extension request.");
            await _policy.ValidateExtensionDurationAsync(entity.EndTime, request.RequestedEndTime, cancellationToken);
            var now = DateTime.UtcNow;
            var extension = new ReservationExtensionRequest
            {
                Id = Guid.NewGuid(), ReservationId = entity.Id,
                CurrentEndTimeSnapshot = entity.EndTime, RequestedEndTime = request.RequestedEndTime,
                RequestedByAccount = account, RequestedByName = entity.ApplicantName,
                RequestedAt = now, CreatedAt = now, UpdatedAt = now
            };
            entity.ExtensionRequests.Add(extension);
            _db.ReservationExtensionRequests.Add(extension);
            AddAudit(entity, NewAudit(entity.Id, ReservationAuditActions.ExtensionRequested,
                entity.Status, entity.Status, account, entity.ApplicantName, now,
                details: $"Requested end: {request.RequestedEndTime:O}"));
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return MapExtension(extension, entity);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            throw;
        }
    }

    public Task<ReservationExtensionRequestDto> ApproveExtensionAsync(
        Guid extensionId, ClaimsPrincipal user, CancellationToken cancellationToken = default) =>
        ReviewExtensionAsync(extensionId, user, approve: true, reason: null, cancellationToken);

    public Task<ReservationExtensionRequestDto> RejectExtensionAsync(
        Guid extensionId, ClaimsPrincipal user, string? reason, CancellationToken cancellationToken = default) =>
        ReviewExtensionAsync(extensionId, user, approve: false, reason, cancellationToken);

    public Task<ReservationExtensionRequestDto> CancelExtensionAsync(
        Guid extensionId, ClaimsPrincipal user, CancellationToken cancellationToken = default) =>
        CancelExtensionCoreAsync(extensionId, user, cancellationToken);

    private async Task<ReservationExtensionRequestDto> ReviewExtensionAsync(
        Guid extensionId,
        ClaimsPrincipal user,
        bool approve,
        string? reason,
        CancellationToken cancellationToken)
    {
        var access = await ResolveReviewAccessAsync(user, cancellationToken);
        var rejectionReason = approve ? null : Require(reason, nameof(reason));
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            var (entity, request) = await FindExtensionForUpdateAsync(extensionId, cancellationToken);
            EnsurePendingExtension(request);
            await EnsureWholeReviewAuthorizationAsync(entity, access, cancellationToken);
            var now = DateTime.UtcNow;
            if (approve)
            {
                await ValidateExtensionFinalAsync(entity, request, cancellationToken);
                entity.ExtendEndTime(request.RequestedEndTime, now);
                request.Approve(access.Account, access.ActorName, now);
            }
            else
            {
                request.Reject(access.Account, access.ActorName, rejectionReason!, now);
            }
            AddAudit(entity, NewAudit(entity.Id,
                approve ? ReservationAuditActions.ExtensionApproved : ReservationAuditActions.ExtensionRejected,
                entity.Status, entity.Status, access.Account, access.ActorName, now, rejectionReason,
                approve ? $"New end: {request.RequestedEndTime:O}" : null));
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return MapExtension(request, entity);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<ReservationExtensionRequestDto> CancelExtensionCoreAsync(
        Guid extensionId, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        EnsureReservationUser(user);
        var account = GetAccount(user);
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            var (entity, request) = await FindExtensionForUpdateAsync(extensionId, cancellationToken);
            EnsureOwner(entity, account);
            EnsurePendingExtension(request);
            var now = DateTime.UtcNow;
            request.Cancel(now);
            AddAudit(entity, NewAudit(entity.Id, ReservationAuditActions.ExtensionCancelled,
                entity.Status, entity.Status, account, entity.ApplicantName, now));
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return MapExtension(request, entity);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<ReservationExtensionReviewQueueDto> GetPendingExtensionsAsync(
        ClaimsPrincipal user,
        ReservationExtensionReviewScope scope,
        Guid? teamOptionId = null,
        CancellationToken cancellationToken = default)
    {
        var access = await ResolveReviewAccessAsync(user, cancellationToken);
        EnsureExtensionReviewScopeAllowed(access, scope);
        var teamScope = await ResolveTeamScopeAsync(
            access,
            scope == ReservationExtensionReviewScope.Team,
            scope == ReservationExtensionReviewScope.All,
            teamOptionId,
            cancellationToken);
        var requests = await _db.ReservationExtensionRequests.AsNoTracking()
            .Include(x => x.Reservation).ThenInclude(x => x.Items)
                .ThenInclude(x => x.Apparatus).ThenInclude(x => x.OwnerTeamOption)
            .Include(x => x.Reservation).ThenInclude(x => x.EquipmentGroup).ThenInclude(x => x!.OwnerTeamOption)
            .AsSplitQuery()
            .Where(x => x.Status == ReservationExtensionRequestStatus.Pending)
            .OrderBy(x => x.RequestedAt)
            .ToListAsync(cancellationToken);
        var custodianProfiles = await ApparatusCustodianResolver.LoadDisplayNamesAsync(
            _db,
            requests.SelectMany(x => x.Reservation.Items)
                .Select(x => x.Apparatus.CustodianAccount),
            cancellationToken);

        var entries = new List<ReservationExtensionRequestDto>();
        var teamPredicate = scope is ReservationExtensionReviewScope.Team or ReservationExtensionReviewScope.All
            ? BuildReservationTeamFilter(teamScope.TeamOptionIds).Compile()
            : null;
        foreach (var request in requests)
        {
            var reservation = request.Reservation;
            if (teamPredicate is not null)
            {
                if (teamPredicate(reservation))
                    entries.Add(MapExtension(request, reservation, access, scope, custodianProfiles));
                continue;
            }

            if (IsEnvironmentReservation(reservation))
                continue;

            if (reservation.Items.Any(x => IsWithinExtensionReviewScope(x.Apparatus, access, scope)))
                entries.Add(MapExtension(request, reservation, access, scope, custodianProfiles));
        }

        return new ReservationExtensionReviewQueueDto
        {
            Scope = scope,
            SelectedTeamOptionId = teamScope.SelectedTeamOptionId,
            IsTeamLeader = access.LeaderTeamIds.Count != 0,
            IsAdmin = access.IsAdmin,
            TeamOptions = teamScope.Options,
            Entries = entries
        };
    }

    public async Task<ReservationOverdueResponseDto> GetOverdueAsync(
        ClaimsPrincipal user,
        ReservationReviewScope scope,
        Guid? teamOptionId = null,
        CancellationToken cancellationToken = default)
    {
        var access = await ResolveReviewAccessAsync(user, cancellationToken);
        EnsureOverdueScopeAllowed(access, scope);
        var teamScope = await ResolveTeamScopeAsync(
            access,
            scope == ReservationReviewScope.Team,
            scope == ReservationReviewScope.All,
            teamOptionId,
            cancellationToken);
        var now = DateTime.UtcNow;

        var source = _db.Reservations.AsNoTracking()
            .Where(x => (x.Status == ReservationStatus.Borrowed || x.Status == ReservationStatus.Approved)
                && x.EndTime < now);

        source = scope switch
        {
            ReservationReviewScope.Custodian => source.Where(x => x.Items.Any(i =>
                i.Apparatus.CustodianAccount != null
                && i.Apparatus.CustodianAccount.ToLower() == access.Account)),
            ReservationReviewScope.Team or ReservationReviewScope.All =>
                source.Where(BuildReservationTeamFilter(teamScope.TeamOptionIds)),
            _ => source
        };

        var items = await source
            .OrderBy(x => x.EndTime)
            .ThenBy(x => x.ReservationNo)
            .Select(x => new ReservationOverdueItemDto
            {
                ReservationId = x.Id,
                ReservationNo = x.ReservationNo,
                Category = ReservationOverdueCategory.OverdueUnreturned,
                ReservationStatus = x.Status,
                ApplicantName = x.ApplicantName,
                ApplicantDepartment = x.ApplicantDepartment,
                ApplicantExtension = x.ApplicantExtension,
                Purpose = x.Purpose,
                StartTime = x.StartTime,
                EndTime = x.EndTime,
                BorrowedAt = x.BorrowedAt,
                TotalReservationItemCount = x.Items.Count,
                VisibleApparatus = x.Items
                    .Where(i => scope != ReservationReviewScope.Custodian
                        || (i.Apparatus.CustodianAccount != null
                            && i.Apparatus.CustodianAccount.ToLower() == access.Account))
                    .OrderBy(i => i.ApparatusId)
                    .Select(i => new ReservationOverdueApparatusDto
                    {
                        Id = i.Apparatus.Id,
                        Name = i.Apparatus.Name,
                        ProductsId = i.Apparatus.ProductsId,
                        Kind = i.Apparatus.Kind,
                        Brand = i.Apparatus.Brand,
                        Model = i.Apparatus.Model,
                        Place = i.Apparatus.Place,
                        Custodian = _db.Users
                            .Where(user => i.Apparatus.CustodianAccount != null
                                && user.Account.ToLower() == i.Apparatus.CustodianAccount.ToLower())
                            .Select(user => user.DisplayName)
                            .FirstOrDefault(),
                        CustodianAccount = i.Apparatus.CustodianAccount,
                        OwnerTeamOptionId = i.Apparatus.OwnerTeamOptionId,
                        OwnerTeamName = i.Apparatus.OwnerTeamOption == null
                            ? null
                            : i.Apparatus.OwnerTeamOption.Name
                    }).ToList()
            })
            .ToListAsync(cancellationToken);

        foreach (var item in items)
            item.VisibleApparatusCount = item.VisibleApparatus.Count;

        return new ReservationOverdueResponseDto
        {
            Scope = scope,
            SelectedTeamOptionId = teamScope.SelectedTeamOptionId,
            IsTeamLeader = access.LeaderTeamIds.Count != 0,
            IsAdmin = access.IsAdmin,
            TeamOptions = teamScope.Options,
            TotalCount = items.Count,
            OverdueReturnCount = items.Count,
            Items = items
        };
    }

    public async Task<ReservationDetailDto> SubmitAsync(Guid id, ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        EnsureReservationUser(user);
        var account = GetAccount(user);
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            var entity = await FindRequiredForUpdateAsync(id, cancellationToken);
            EnsureOwner(entity, account);
            EnsureDirectSingleApparatus(entity);
            await ValidateFinalApprovalAsync(entity, cancellationToken);
            var now = DateTime.UtcNow;
            entity.Submit(now);
            AddAudit(entity, NewAudit(entity.Id, ReservationAuditActions.Submitted, ReservationStatus.Draft,
                ReservationStatus.Pending, account, entity.ApplicantName, now));
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return MapDetail(entity);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            throw;
        }
    }

    public Task<ReservationDetailDto> ApproveAsync(
        Guid id,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default) =>
        ReviewReservationAsync(id, user, approve: true, reason: null, cancellationToken);

    public Task<ReservationDetailDto> RejectAsync(
        Guid id,
        ClaimsPrincipal user,
        string? reason,
        CancellationToken cancellationToken = default) =>
        ReviewReservationAsync(id, user, approve: false, reason, cancellationToken);

    public async Task<ReservationDetailDto> CancelAsync(
        Guid id,
        ClaimsPrincipal user,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        EnsureReservationUser(user);
        var account = GetAccount(user);
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            var entity = await FindRequiredForUpdateAsync(id, cancellationToken);
            EnsureOwner(entity, account);
            var fromStatus = entity.Status;
            var now = DateTime.UtcNow;
            entity.Cancel(account, reason, now);
            foreach (var pendingExtension in entity.ExtensionRequests.Where(x => x.Status == ReservationExtensionRequestStatus.Pending))
            {
                pendingExtension.Cancel(now);
                AddAudit(entity, NewAudit(entity.Id, ReservationAuditActions.ExtensionCancelled,
                    fromStatus, ReservationStatus.Cancelled, account, entity.ApplicantName, now,
                    details: "Cancelled because the reservation was cancelled."));
            }
            AddAudit(entity, NewAudit(entity.Id, ReservationAuditActions.Cancelled, fromStatus,
                ReservationStatus.Cancelled, account, entity.ApplicantName, now, reason));
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return MapDetail(entity);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            throw;
        }
    }

    public Task<ReservationDetailDto> CheckoutAsync(Guid id, ClaimsPrincipal user, CancellationToken cancellationToken = default) =>
        TransitionStaffAsync(id, user, ReservationAuditActions.CheckedOut, null, (x, account, now) => x.Checkout(account, now), cancellationToken);

    public Task<ReservationDetailDto> ReturnAsync(Guid id, ClaimsPrincipal user, CancellationToken cancellationToken = default) =>
        TransitionStaffAsync(id, user, ReservationAuditActions.Returned, null, (x, account, now) => x.Return(account, now), cancellationToken);

    private async Task<ReservationDetailDto> ReviewReservationAsync(
        Guid id,
        ClaimsPrincipal user,
        bool approve,
        string? reason,
        CancellationToken cancellationToken)
    {
        var access = await ResolveReviewAccessAsync(user, cancellationToken);
        if (!approve && string.IsNullOrWhiteSpace(reason))
            throw new InvalidOperationException("Reject reason is required.");
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            var entity = await FindRequiredForUpdateAsync(id, cancellationToken);
            await EnsureWholeReviewAuthorizationAsync(entity, access, cancellationToken);

            var fromStatus = entity.Status;
            var now = DateTime.UtcNow;
            if (approve)
            {
                await ValidateFinalApprovalAsync(entity, cancellationToken);
                entity.Approve(access.Account, now);
            }
            else
            {
                entity.Reject(access.Account, reason, now);
            }

            AddAudit(entity, NewAudit(entity.Id,
                approve ? ReservationAuditActions.Approved : ReservationAuditActions.Rejected,
                fromStatus, entity.Status, access.Account, access.ActorName, now, reason));
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return MapDetail(entity);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<ReservationDetailDto> TransitionStaffAsync(
        Guid id,
        ClaimsPrincipal user,
        string action,
        string? reason,
        Action<Reservation, string, DateTime> transition,
        CancellationToken cancellationToken)
    {
        EnsureScope(user, SystemAuthorization.AccessScopes.CsitStaff);
        var account = GetAccount(user);
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            var entity = await FindRequiredForUpdateAsync(id, cancellationToken);
            var fromStatus = entity.Status;
            var now = DateTime.UtcNow;
            transition(entity, account, now);
            AddAudit(entity, NewAudit(entity.Id, action, fromStatus, entity.Status,
                account, GetActorName(user), now, reason));
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return MapDetail(entity);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<Reservation> FindRequiredForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await _db.Reservations
            .FromSqlInterpolated($"SELECT * FROM reservations WHERE id = {id} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException($"Reservation {id} was not found.");
        await _db.Entry(entity).Collection(x => x.Items).LoadAsync(cancellationToken);
        await _db.Entry(entity).Collection(x => x.ExtensionRequests).LoadAsync(cancellationToken);
        await _db.Entry(entity).Collection(x => x.AuditEvents).LoadAsync(cancellationToken);
        return entity;
    }

    private async Task<(Reservation Reservation, ReservationExtensionRequest Extension)> FindExtensionForUpdateAsync(
        Guid extensionId,
        CancellationToken cancellationToken)
    {
        var reservationId = await _db.ReservationExtensionRequests.AsNoTracking()
            .Where(x => x.Id == extensionId)
            .Select(x => (Guid?)x.ReservationId)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException($"Extension request {extensionId} was not found.");
        var reservation = await FindRequiredForUpdateAsync(reservationId, cancellationToken);
        var extension = await _db.ReservationExtensionRequests
            .FromSqlInterpolated($"SELECT * FROM reservation_extension_requests WHERE id = {extensionId} FOR UPDATE")
            .SingleAsync(cancellationToken);
        return (reservation, extension);
    }

    private static void EnsurePendingExtension(ReservationExtensionRequest request)
    {
        if (request.Status != ReservationExtensionRequestStatus.Pending)
            throw new InvalidOperationException("Only a Pending extension request can be processed.");
    }

    private async Task EnsureWholeReviewAuthorizationAsync(
        Reservation reservation,
        ReviewAccess access,
        CancellationToken cancellationToken)
    {
        if (IsEnvironmentReservation(reservation))
        {
            if (!reservation.EquipmentGroupId.HasValue)
                throw new InvalidOperationException("The Environment reservation no longer has an Equipment Group.");
            var group = await _db.EquipmentGroups
                .FromSqlInterpolated($"SELECT * FROM equipment_groups WHERE id = {reservation.EquipmentGroupId.Value} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException("The Environment reservation's Equipment Group no longer exists.");
            if (!access.IsAdmin
                && !await HasEnabledTeamLeadershipForUpdateAsync(group.OwnerTeamOptionId, access.Account, cancellationToken))
                throw new UnauthorizedAccessException("Only the current Environment Group Owner Team Leader or Admin may review this request.");
            return;
        }

        if (reservation.Items.Count != 1)
            throw new InvalidOperationException(DirectSingleApparatusError);
        var apparatusId = reservation.Items[0].ApparatusId;
        await LockApparatusAsync([apparatusId], cancellationToken);
        var apparatus = await _db.Apparatuses.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == apparatusId, cancellationToken)
            ?? throw new InvalidOperationException($"Apparatus {apparatusId} does not exist.");
        if (!await CanReviewApparatusForUpdateAsync(apparatus, access, cancellationToken))
            throw new UnauthorizedAccessException("Only the current apparatus custodian, owner Team leader, or Admin may review this request.");
    }

    private async Task ValidateExtensionFinalAsync(
        Reservation entity,
        ReservationExtensionRequest request,
        CancellationToken cancellationToken)
    {
        if (entity.Status is not ReservationStatus.Approved and not ReservationStatus.Borrowed)
            throw new InvalidOperationException("The reservation is no longer eligible for extension.");
        if (entity.EndTime != request.CurrentEndTimeSnapshot)
            throw new InvalidOperationException("Reservation EndTime changed after this extension request was created.");
        await _policy.ValidateExtensionDurationAsync(entity.EndTime, request.RequestedEndTime, cancellationToken);

        string[] apparatusIds;
        if (IsGroupEnvironmentReservation(entity))
        {
            var prepared = await PrepareEnvironmentGroupAsync(entity.EquipmentGroupId!.Value, cancellationToken);
            apparatusIds = prepared.ApparatusIds;
            EnsureEnvironmentCompositionUnchanged(entity.Items, apparatusIds);
            await EnsureEnvironmentNoOverlapAsync(
                apparatusIds, entity.EndTime, request.RequestedEndTime, entity.Id, cancellationToken);
        }
        else
        {
            apparatusIds = entity.Items.Select(x => x.ApparatusId)
                .OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (apparatusIds.Length == 0)
                throw new InvalidOperationException("A reservation must contain at least one apparatus.");
            await LockApparatusAsync(apparatusIds, cancellationToken);
            await LoadAndValidateApparatusAsync(apparatusIds, cancellationToken);
            if (!IsEnvironmentReservation(entity))
                await _environmentGroupDevices.EnsureDirectReservationAllowedAsync(apparatusIds, cancellationToken);
            await _availability.EnsureBookableAsync(apparatusIds, cancellationToken);
            await _availability.EnsureNoOverlapAsync(
                apparatusIds, entity.EndTime, request.RequestedEndTime, entity.Id, cancellationToken);
        }
        await _policy.EnsureDepartmentQuotaAsync(
            entity.ApplicantDepartment, entity.EndTime, request.RequestedEndTime, apparatusIds.Length, entity.Id,
            acquireTransactionLock: true, cancellationToken: cancellationToken);
    }

    private async Task ValidateFinalApprovalAsync(Reservation entity, CancellationToken cancellationToken)
    {
        ValidateTimeRange(entity.StartTime, entity.EndTime);
        Require(entity.ApplicantExtension, nameof(entity.ApplicantExtension));
        await _policy.ValidateInitialDurationAsync(entity.StartTime, entity.EndTime, cancellationToken);

        string[] apparatusIds;
        if (IsGroupEnvironmentReservation(entity))
        {
            var prepared = await PrepareEnvironmentGroupAsync(entity.EquipmentGroupId!.Value, cancellationToken);
            apparatusIds = prepared.ApparatusIds;
            EnsureEnvironmentCompositionUnchanged(entity.Items, apparatusIds);
            await EnsureEnvironmentNoOverlapAsync(
                apparatusIds, entity.StartTime, entity.EndTime, entity.Id, cancellationToken);
        }
        else
        {
            await EnsureStoredEnvironmentSelectionsValidAsync(entity, cancellationToken);
            apparatusIds = entity.Items.Select(x => x.ApparatusId)
                .OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (apparatusIds.Length == 0)
                throw new InvalidOperationException("A reservation must contain at least one apparatus.");
            await LockApparatusAsync(apparatusIds, cancellationToken);
            await LoadAndValidateApparatusAsync(apparatusIds, cancellationToken);
            if (!IsEnvironmentReservation(entity))
                await _environmentGroupDevices.EnsureDirectReservationAllowedAsync(apparatusIds, cancellationToken);
            await _availability.EnsureBookableAsync(apparatusIds, cancellationToken);
            await _availability.EnsureNoOverlapAsync(
                apparatusIds, entity.StartTime, entity.EndTime, entity.Id, cancellationToken);
        }

        await _policy.EnsureDepartmentQuotaAsync(
            entity.ApplicantDepartment, entity.StartTime, entity.EndTime, apparatusIds.Length, entity.Id,
            acquireTransactionLock: true, cancellationToken: cancellationToken);
        await EnsureApplicationOptionIsActiveAsync(
            entity.Customer, SystemOptionCategories.Customer, nameof(entity.Customer), cancellationToken);
        await EnsureApplicationOptionIsActiveAsync(
            entity.ProjectSubPu, SystemOptionCategories.SubPu, nameof(entity.ProjectSubPu), cancellationToken);
    }

    private async Task<bool> CanReviewApparatusForUpdateAsync(
        Apparatus apparatus,
        ReviewAccess access,
        CancellationToken cancellationToken)
    {
        if (access.IsAdmin) return true;
        if (!string.IsNullOrWhiteSpace(apparatus.CustodianAccount)
            && string.Equals(apparatus.CustodianAccount.Trim(), access.Account, StringComparison.OrdinalIgnoreCase))
            return true;
        return apparatus.OwnerTeamOptionId.HasValue
            && await HasEnabledTeamLeadershipForUpdateAsync(
                apparatus.OwnerTeamOptionId.Value, access.Account, cancellationToken);
    }

    private async Task<bool> HasEnabledTeamLeadershipForUpdateAsync(
        Guid teamOptionId,
        string account,
        CancellationToken cancellationToken) =>
        (await _db.TeamRoutings
            .FromSqlInterpolated($"SELECT * FROM team_routings WHERE team_option_id = {teamOptionId} AND is_enabled AND lower(leader_account) = {account} FOR SHARE")
            .AsNoTracking()
            .ToListAsync(cancellationToken)).Count != 0;

    private async Task LockApparatusAsync(IReadOnlyCollection<string> apparatusIds, CancellationToken cancellationToken)
    {
        var orderedIds = apparatusIds.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM apparatus WHERE \"Id\" = ANY ({orderedIds}) ORDER BY \"Id\" FOR UPDATE",
            cancellationToken);
    }

    private async Task<List<Apparatus>> LoadAndValidateApparatusAsync(
        IReadOnlyCollection<string> apparatusIds,
        CancellationToken cancellationToken)
    {
        var apparatuses = await _db.Apparatuses
            .Where(x => apparatusIds.Contains(x.Id))
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        if (apparatuses.Count != apparatusIds.Count)
        {
            var found = apparatuses.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            var missing = apparatusIds.Where(x => !found.Contains(x));
            throw new InvalidOperationException($"Apparatus does not exist: {string.Join(", ", missing)}.");
        }

        return apparatuses;
    }

    private async Task<AppUser> ResolveApplicantAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var account = GetAccount(user);
        return await _db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Account == account, cancellationToken)
            ?? throw new InvalidOperationException("Authenticated user profile was not found.");
    }

    private async Task EnsureApplicationOptionIsActiveAsync(
        string? value,
        string category,
        string fieldName,
        CancellationToken cancellationToken)
    {
        var canonicalValue = Require(value, fieldName);
        if (!await _db.SystemOptions.AsNoTracking().AnyAsync(
                x => x.Category == category && x.IsEnabled && x.Value == canonicalValue,
                cancellationToken))
            throw new InvalidOperationException($"{fieldName} must be a currently enabled {category} option.");
    }

    private static ReservationOptionDto ToReservationOption(string value, string name) => new()
    {
        Value = value,
        Label = string.IsNullOrWhiteSpace(name) ? value : name
    };

    private async Task EnsureStoredEnvironmentSelectionsValidAsync(
        Reservation entity,
        CancellationToken cancellationToken)
    {
        if (!entity.TestExecutionProfileId.HasValue) return;
        var profile = await _db.TestExecutionProfiles.AsNoTracking()
            .Include(x => x.TestEnvironment)
            .Include(x => x.EquipmentGroup).ThenInclude(x => x.Requirements)
            .SingleOrDefaultAsync(x => x.Id == entity.TestExecutionProfileId.Value, cancellationToken)
            ?? throw new InvalidOperationException("Test execution profile does not exist.");
        if (profile.Status != TestExecutionProfileStatus.Active)
            throw new InvalidOperationException("Test execution profile is not Active.");
        if (profile.TestEnvironment.Status != TestEnvironmentStatus.Active)
            throw new InvalidOperationException("Test environment is not Active.");
        if (profile.EquipmentGroup.Status != EquipmentGroupStatus.Active)
            throw new InvalidOperationException("Equipment group is not Active.");

        var selectedByRequirement = entity.Items
            .Where(x => x.EquipmentGroupRequirementId.HasValue)
            .GroupBy(x => x.EquipmentGroupRequirementId!.Value)
            .ToDictionary(x => x.Key, x => x.Select(item => item.ApparatusId).ToArray());
        if (selectedByRequirement.Values.Sum(x => x.Length) != entity.Items.Count)
            throw new InvalidOperationException("Environment reservation contains an item without an equipment group requirement.");

        foreach (var requirement in profile.EquipmentGroup.Requirements)
        {
            selectedByRequirement.TryGetValue(requirement.Id, out var selectedIds);
            selectedIds ??= [];
            if (!string.IsNullOrWhiteSpace(requirement.PreferredEquipmentId))
            {
                var preferredMatches = await _resourceCapabilities.GetMatchingApparatusIdsAsync(
                    requirement.ResourceType,
                    requirement.CapabilityTag,
                    [requirement.PreferredEquipmentId],
                    cancellationToken);
                if (!preferredMatches.Contains(requirement.PreferredEquipmentId))
                    throw new InvalidOperationException(
                        $"Catalog configuration error: preferred equipment for {requirement.ResourceType} does not match its resource capability.");
            }
            if (requirement.Required && selectedIds.Length != requirement.Quantity)
                throw new InvalidOperationException($"Required requirement {requirement.ResourceType} must select exactly {requirement.Quantity} apparatus.");
            if (!requirement.Required && selectedIds.Length != 0 && selectedIds.Length != requirement.Quantity)
                throw new InvalidOperationException($"Optional requirement {requirement.ResourceType} must select either zero or exactly {requirement.Quantity} apparatus.");
            if (!requirement.AllowAlternative
                && selectedIds.Any(id => !string.Equals(id, requirement.PreferredEquipmentId, StringComparison.Ordinal)))
                throw new InvalidOperationException($"Requirement {requirement.ResourceType} must use its preferred equipment.");
            if (selectedIds.Length == 0) continue;
            var matchingIds = await _resourceCapabilities.GetMatchingApparatusIdsAsync(
                requirement.ResourceType,
                requirement.CapabilityTag,
                selectedIds,
                cancellationToken);
            var mismatched = selectedIds.Where(x => !matchingIds.Contains(x)).ToArray();
            if (mismatched.Length != 0)
                throw new InvalidOperationException(
                    $"Selected apparatus does not match requirement {requirement.ResourceType}{FormatCapability(requirement.CapabilityTag)}: {string.Join(", ", mismatched)}.");
        }

        var requirementIds = profile.EquipmentGroup.Requirements.Select(x => x.Id).ToHashSet();
        if (selectedByRequirement.Keys.Any(x => !requirementIds.Contains(x)))
            throw new InvalidOperationException("A selected requirement does not belong to the profile's equipment group.");
    }

    private async Task<PreparedReservation> PrepareEnvironmentGroupAsync(
        Guid groupId,
        CancellationToken cancellationToken)
    {
        // Lock the group first. Besides stabilizing its enabled state, PostgreSQL's
        // FK key-share lock makes concurrent membership inserts wait for this flow.
        var group = await _db.EquipmentGroups
            .FromSqlInterpolated($"SELECT * FROM equipment_groups WHERE id = {groupId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("測試環境群組不存在。");

        var memberships = await _db.EquipmentGroupDevices
            .FromSqlInterpolated($"SELECT * FROM equipment_group_devices WHERE equipment_group_id = {groupId} ORDER BY apparatus_id FOR UPDATE")
            .ToListAsync(cancellationToken);
        var apparatusIds = memberships.Select(x => x.ApparatusId)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        await LockApparatusAsync(apparatusIds, cancellationToken);
        var apparatuses = await LoadAndValidateApparatusAsync(apparatusIds, cancellationToken);

        if (group.Status != EquipmentGroupStatus.Active)
            throw new InvalidOperationException("測試環境群組目前未啟用。");

        var completeness = EnvironmentGroupDeviceRules.GetCompletenessStatus(
            memberships.Count,
            memberships.Count(x => x.IsInEnvironment));
        if (completeness != EquipmentGroupCompletenessStatus.Complete)
        {
            var apparatusById = apparatuses.ToDictionary(x => x.Id, StringComparer.Ordinal);
            var missing = memberships.Where(x => !x.IsInEnvironment)
                .Select(x => apparatusById[x.ApparatusId])
                .Select(DeviceLabel)
                .ToArray();
            var details = missing.Length == 0 ? "尚未設定設備" : string.Join("、", missing);
            throw new InvalidOperationException($"測試環境設備不齊全：{details}。");
        }

        try
        {
            await _availability.EnsureBookableAsync(apparatusIds, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            var unavailable = apparatuses
                .Where(x => !ApparatusReservationRules.IsBookable(x))
                .Select(x => $"{DeviceLabel(x)}（{x.ReservationStatus ?? "未設定"}）")
                .ToArray();
            throw new InvalidOperationException(
                $"設備目前狀態不可使用：{string.Join("、", unavailable)}。", ex);
        }

        var items = await CreateReservationItemsAsync(
            apparatuses.OrderBy(x => x.Id, StringComparer.Ordinal),
            cancellationToken);
        var context = new ReservationEnvironmentContext(
            group.Id,
            group.Code,
            group.Name);
        return new PreparedReservation(context, items);
    }

    private async Task EnsureEnvironmentNoOverlapAsync(
        IReadOnlyCollection<string> apparatusIds,
        DateTime startTime,
        DateTime endTime,
        Guid? excludedReservationId,
        CancellationToken cancellationToken)
    {
        try
        {
            await _availability.EnsureNoOverlapAsync(
                apparatusIds, startTime, endTime, excludedReservationId, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException("指定時段已有設備被預約。", ex);
        }
    }

    private static void EnsureEnvironmentCompositionUnchanged(
        IReadOnlyCollection<ReservationItem> storedItems,
        IReadOnlyCollection<string> currentApparatusIds)
    {
        var stored = storedItems.Select(x => x.ApparatusId)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        var current = currentApparatusIds.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (!stored.SequenceEqual(current, StringComparer.Ordinal))
            throw new InvalidOperationException("測試環境設備組成已變更，請重新確認並更新預約。");
    }

    private async Task<PreparedReservation> PrepareRequestAsync(
        ReservationMode mode,
        IReadOnlyCollection<ReservationItemRequest>? directItems,
        Guid? environmentGroupId,
        Guid? profileId,
        IReadOnlyCollection<ReservationRequirementSelectionRequest>? selections,
        CancellationToken cancellationToken)
    {
        if (mode == ReservationMode.Direct)
        {
            if (environmentGroupId.HasValue || profileId.HasValue || selections?.Count > 0)
                throw new InvalidOperationException("Direct reservations cannot include environment selections.");
            if (directItems?.Count > 1)
                throw new InvalidOperationException(DirectSingleApparatusError);
            var ids = NormalizeApparatusIds(directItems);
            await LockApparatusAsync(ids, cancellationToken);
            var directApparatuses = await LoadAndValidateApparatusAsync(ids, cancellationToken);
            await _environmentGroupDevices.EnsureDirectReservationAllowedAsync(ids, cancellationToken);
            await _availability.EnsureBookableAsync(ids, cancellationToken);
            return new PreparedReservation(
                null,
                await CreateReservationItemsAsync(directApparatuses, cancellationToken));
        }

        if (mode != ReservationMode.Environment)
            throw new InvalidOperationException("Reservation mode is invalid.");
        if (!environmentGroupId.HasValue || environmentGroupId == Guid.Empty)
            throw new InvalidOperationException("EnvironmentGroupId is required for an Environment reservation.");
        if (profileId.HasValue || selections?.Count > 0)
            throw new InvalidOperationException("新版測試環境預約不可包含舊版測試方案或設備需求選擇。");

        // ApparatusIds supplied by the caller are intentionally ignored. The current
        // EquipmentGroupDevice membership is the sole source of ReservationItems.
        return await PrepareEnvironmentGroupAsync(environmentGroupId.Value, cancellationToken);
    }

    private static string[] NormalizeApparatusIds(IReadOnlyCollection<ReservationItemRequest>? items)
    {
        if (items is null || items.Count == 0)
            throw new InvalidOperationException("At least one apparatus is required.");
        var ids = items.Select(x => Require(x?.ApparatusId, nameof(ReservationItemRequest.ApparatusId))).ToArray();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new InvalidOperationException("The same apparatus cannot be added more than once.");
        return ids.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    private static void EnsureDirectSingleApparatus(Reservation reservation)
    {
        if (!IsEnvironmentReservation(reservation) && reservation.Items.Count > 1)
            throw new InvalidOperationException(DirectSingleApparatusError);
    }

    private async Task<List<ReservationItem>> CreateReservationItemsAsync(
        IEnumerable<Apparatus> apparatuses,
        CancellationToken cancellationToken)
    {
        var apparatusList = apparatuses.ToList();
        var custodianProfiles = await ApparatusCustodianResolver.LoadDisplayNamesAsync(
            _db,
            apparatusList.Select(x => x.CustodianAccount),
            cancellationToken);
        return apparatusList.Select(x => ToReservationItem(
            x,
            null,
            ApparatusCustodianResolver.GetDisplayName(custodianProfiles, x.CustodianAccount),
            ApparatusCustodianResolver.GetDepartment(custodianProfiles, x.CustodianAccount))).ToList();
    }

    private static ReservationItem ToReservationItem(
        Apparatus x,
        EquipmentGroupRequirement? requirement,
        string? custodianDisplayName,
        string? custodianDepartment) => new()
    {
        Id = Guid.NewGuid(),
        ApparatusId = x.Id,
        ApparatusName = x.Name,
        ProductsId = x.ProductsId,
        Kind = x.Kind,
        Brand = x.Brand,
        Model = x.Model,
        Number = x.Number,
        Place = x.Place,
        Custodian = Clean(custodianDisplayName),
        CustodianDepartment = Clean(custodianDepartment),
        PriceUse = x.PriceUse,
        EquipmentGroupRequirementId = requirement?.Id,
        RequirementResourceTypeSnapshot = requirement?.ResourceType,
        RequirementCapabilityTagSnapshot = requirement?.CapabilityTag
    };

    private static void ValidateTimeRange(DateTime startTime, DateTime endTime)
    {
        if (startTime.Kind != DateTimeKind.Utc || endTime.Kind != DateTimeKind.Utc)
            throw new InvalidOperationException("StartTime and EndTime must be UTC values.");
        if (startTime >= endTime)
            throw new InvalidOperationException("StartTime must be earlier than EndTime.");
    }

    private static void EnsureOwner(Reservation entity, string account)
    {
        if (!string.Equals(entity.ApplicantAccount, account, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The reservation does not belong to the authenticated applicant.");
    }

    private static string GetKnownScope(ClaimsPrincipal user)
    {
        var scope = user.FindFirstValue(SystemAuthorization.AccessScopeClaim);
        if (scope is SystemAuthorization.AccessScopes.RdApplicant or SystemAuthorization.AccessScopes.CsitStaff)
            return scope;
        throw new UnauthorizedAccessException("A recognized access scope is required.");
    }

    private static void EnsureScope(ClaimsPrincipal user, string requiredScope)
    {
        if (!string.Equals(GetKnownScope(user), requiredScope, StringComparison.Ordinal))
            throw new UnauthorizedAccessException($"The {requiredScope} access scope is required.");
    }

    private static void EnsureReservationUser(ClaimsPrincipal user) => _ = GetKnownScope(user);

    private async Task<ReviewAccess> ResolveReviewAccessAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        EnsureScope(user, SystemAuthorization.AccessScopes.CsitStaff);
        var account = GetAccount(user);
        var teamIds = await _db.TeamRoutings.AsNoTracking()
            .Where(x => x.IsEnabled && x.LeaderAccount.ToLower() == account)
            .Select(x => x.TeamOptionId)
            .Distinct()
            .ToListAsync(cancellationToken);
        return new ReviewAccess(account, GetActorName(user), user.IsInRole("Admin"), teamIds.ToHashSet());
    }

    private async Task<TeamScopeContext> ResolveTeamScopeAsync(
        ReviewAccess access,
        bool isLeaderTeamScope,
        bool isAllTeamScope,
        Guid? selectedTeamOptionId,
        CancellationToken cancellationToken)
    {
        if (!isLeaderTeamScope && !isAllTeamScope)
            return new TeamScopeContext([], null, []);

        var query = _db.SystemOptions.AsNoTracking()
            .Where(x => x.Category == SystemOptionCategories.Team && x.IsEnabled);

        if (isLeaderTeamScope)
            query = query.Where(x => access.LeaderTeamIds.Contains(x.Id));

        var options = await query
            .OrderBy(x => x.Sort)
            .ThenBy(x => x.Name)
            .ThenBy(x => x.Value)
            .Select(x => new ReservationTeamScopeOptionDto
            {
                TeamOptionId = x.Id,
                DisplayName = x.Name,
                Value = x.Value
            })
            .ToListAsync(cancellationToken);

        if (selectedTeamOptionId.HasValue
            && options.All(x => x.TeamOptionId != selectedTeamOptionId.Value))
            throw new UnauthorizedAccessException("The selected Team is outside the available Team scope.");

        var effectiveTeamOptionId = selectedTeamOptionId;
        if (isLeaderTeamScope && !effectiveTeamOptionId.HasValue && options.Count == 1)
            effectiveTeamOptionId = options[0].TeamOptionId;

        var teamOptionIds = effectiveTeamOptionId.HasValue
            ? new[] { effectiveTeamOptionId.Value }
            : options.Select(x => x.TeamOptionId).ToArray();

        return new TeamScopeContext(options, effectiveTeamOptionId, teamOptionIds);
    }

    private static Expression<Func<Reservation, bool>> BuildReservationTeamFilter(
        IReadOnlyCollection<Guid> teamOptionIds)
    {
        var ids = teamOptionIds.ToArray();
        return reservation =>
            ((!reservation.EquipmentGroupId.HasValue && !reservation.TestExecutionProfileId.HasValue)
                && reservation.Items.Any(item => item.Apparatus.OwnerTeamOptionId.HasValue
                    && ids.Contains(item.Apparatus.OwnerTeamOptionId.Value)))
            || ((reservation.EquipmentGroupId.HasValue || reservation.TestExecutionProfileId.HasValue)
                && reservation.EquipmentGroup != null
                && ids.Contains(reservation.EquipmentGroup.OwnerTeamOptionId));
    }

    private static void EnsureReviewScopeAllowed(ReviewAccess access, ReservationReviewScope scope)
    {
        if (scope == ReservationReviewScope.Team && access.LeaderTeamIds.Count == 0)
            throw new UnauthorizedAccessException("An enabled Team leader routing is required for the Team review queue.");
        if (scope == ReservationReviewScope.All && !access.IsAdmin)
            throw new UnauthorizedAccessException("Admin role is required for the all-reservations review queue.");
        if (!Enum.IsDefined(scope))
            throw new ArgumentException("Review scope is invalid.", nameof(scope));
    }

    private static void EnsureOverdueScopeAllowed(ReviewAccess access, ReservationReviewScope scope)
    {
        if (scope == ReservationReviewScope.Team && access.LeaderTeamIds.Count == 0)
            throw new UnauthorizedAccessException("An enabled Team leader routing is required for the Team overdue scope.");
        if (scope == ReservationReviewScope.All && !access.IsAdmin)
            throw new UnauthorizedAccessException("Admin role is required for the all-overdue scope.");
        if (!Enum.IsDefined(scope))
            throw new ArgumentException("Overdue scope is invalid.", nameof(scope));
    }

    private static void EnsureExtensionReviewScopeAllowed(
        ReviewAccess access,
        ReservationExtensionReviewScope scope)
    {
        if (scope == ReservationExtensionReviewScope.Team && access.LeaderTeamIds.Count == 0)
            throw new UnauthorizedAccessException("An enabled Team leader routing is required for the Team extension queue.");
        if (scope == ReservationExtensionReviewScope.All && !access.IsAdmin)
            throw new UnauthorizedAccessException("Admin role is required for the all-extensions queue.");
        if (!Enum.IsDefined(scope))
            throw new ArgumentException("Extension review scope is invalid.", nameof(scope));
    }

    private static string GetActorName(ClaimsPrincipal user)
    {
        var value = user.FindFirstValue("display_name")
            ?? user.FindFirstValue(ClaimTypes.Name)
            ?? GetAccount(user);
        return string.IsNullOrWhiteSpace(value) ? GetAccount(user) : value.Trim();
    }

    private static string GetAccount(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true) throw new UnauthorizedAccessException("Authentication is required.");
        var account = user.FindFirstValue("account") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        return string.IsNullOrWhiteSpace(account)
            ? throw new UnauthorizedAccessException("Authenticated account claim is missing.")
            : account.Trim().ToLowerInvariant();
    }

    private static string Require(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"{name} is required.") : value.Trim();

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string DeviceLabel(Apparatus apparatus) =>
        $"{(string.IsNullOrWhiteSpace(apparatus.ProductsId) ? apparatus.Id : apparatus.ProductsId)} / {apparatus.Name}";

    private static bool IsEnvironmentReservation(Reservation reservation) =>
        reservation.EquipmentGroupId.HasValue || reservation.TestExecutionProfileId.HasValue;

    private static bool IsGroupEnvironmentReservation(Reservation reservation) =>
        reservation.EquipmentGroupId.HasValue && !reservation.TestExecutionProfileId.HasValue;

    private static bool IsWithinReviewScope(
        Apparatus apparatus,
        ReviewAccess access,
        ReservationReviewScope scope) => scope switch
        {
            ReservationReviewScope.Custodian => !string.IsNullOrWhiteSpace(apparatus.CustodianAccount)
                && string.Equals(apparatus.CustodianAccount.Trim(), access.Account, StringComparison.OrdinalIgnoreCase),
            ReservationReviewScope.Team => apparatus.OwnerTeamOptionId.HasValue
                && access.LeaderTeamIds.Contains(apparatus.OwnerTeamOptionId.Value),
            ReservationReviewScope.All => access.IsAdmin,
            _ => false
        };

    private static bool IsWithinExtensionReviewScope(
        Apparatus apparatus,
        ReviewAccess access,
        ReservationExtensionReviewScope scope) => scope switch
        {
            ReservationExtensionReviewScope.Custodian => !string.IsNullOrWhiteSpace(apparatus.CustodianAccount)
                && string.Equals(apparatus.CustodianAccount.Trim(), access.Account, StringComparison.OrdinalIgnoreCase),
            ReservationExtensionReviewScope.Team => apparatus.OwnerTeamOptionId.HasValue
                && access.LeaderTeamIds.Contains(apparatus.OwnerTeamOptionId.Value),
            ReservationExtensionReviewScope.All => access.IsAdmin,
            _ => false
        };

    private static bool CanViewReview(
        Reservation reservation,
        ReviewAccess access,
        ReservationReviewScope scope)
    {
        if (!IsEnvironmentReservation(reservation))
            return reservation.Items.Any(x => IsWithinReviewScope(x.Apparatus, access, scope));
        return scope switch
        {
            ReservationReviewScope.Team => reservation.EquipmentGroup is not null
                && access.LeaderTeamIds.Contains(reservation.EquipmentGroup.OwnerTeamOptionId),
            ReservationReviewScope.All => access.IsAdmin,
            _ => false
        };
    }

    private static ReservationReviewEntryDto MapReviewEntry(
        Reservation reservation,
        ReviewAccess access,
        ReservationReviewScope scope,
        IReadOnlyDictionary<string, ApparatusCustodianProfile> custodianProfiles)
    {
        var detail = MapReviewDetail(reservation, access, scope, custodianProfiles);
        return new ReservationReviewEntryDto
        {
            ReservationId = detail.Id,
            ReservationNo = detail.ReservationNo,
            ApplicantName = detail.ApplicantName,
            ApplicantDepartment = detail.ApplicantDepartment,
            Purpose = detail.Purpose,
            StartTime = detail.StartTime,
            EndTime = detail.EndTime,
            CreatedAt = detail.CreatedAt,
            Status = detail.Status,
            Mode = detail.Mode,
            TotalItemCount = detail.Items.Count,
            ApparatusNames = detail.Items.Select(x => x.ApparatusName).ToList(),
            EquipmentGroupId = detail.EquipmentGroupId,
            EquipmentGroupName = detail.EquipmentGroupNameSnapshot,
            OwnerTeamOptionId = detail.CurrentOwnerTeamOptionId,
            OwnerTeamName = detail.CurrentOwnerTeamName,
            CanApprove = detail.CanApprove,
            CanReject = detail.CanReject
        };
    }

    private static ReservationDetailDto MapReviewDetail(
        Reservation reservation,
        ReviewAccess access,
        ReservationReviewScope scope,
        IReadOnlyDictionary<string, ApparatusCustodianProfile> custodianProfiles)
    {
        var detail = MapDetail(reservation);
        var extensionScope = (ReservationExtensionReviewScope)(int)scope;
        detail.ExtensionRequests = reservation.ExtensionRequests.OrderByDescending(x => x.RequestedAt)
            .Select(x => MapExtension(x, reservation, access, extensionScope, custodianProfiles)).ToList();
        if (detail.Mode == ReservationMode.Direct)
        {
            detail.Items = reservation.Items.OrderBy(x => x.ApparatusId)
                .Select(x => MapReviewItem(x, custodianProfiles)).ToList();
            var directApparatus = reservation.Items.OrderBy(x => x.ApparatusId)
                .Select(x => x.Apparatus)
                .FirstOrDefault();
            detail.CurrentOwnerTeamOptionId = directApparatus?.OwnerTeamOptionId;
            detail.CurrentOwnerTeamName = directApparatus?.OwnerTeamOption?.Name;
            var hasDirectScopeAccess = reservation.Items.Any(x => IsWithinReviewScope(x.Apparatus, access, scope));
            detail.CanApprove = reservation.Status == ReservationStatus.Pending && hasDirectScopeAccess;
            detail.CanReject = detail.CanApprove;
            detail.ReviewResponsibility = "設備保管人、設備 Owner Team Leader 或 Admin（整張審核）";
            return detail;
        }

        var group = reservation.EquipmentGroup;
        detail.CurrentOwnerTeamOptionId = group?.OwnerTeamOptionId;
        detail.CurrentOwnerTeamName = group?.OwnerTeamOption?.Name;
        var hasScopeAccess = scope == ReservationReviewScope.All
            ? access.IsAdmin
            : scope == ReservationReviewScope.Team && group is not null
                && access.LeaderTeamIds.Contains(group.OwnerTeamOptionId);
        detail.CanApprove = reservation.Status == ReservationStatus.Pending && hasScopeAccess;
        detail.CanReject = detail.CanApprove;
        detail.ReviewResponsibility = string.IsNullOrWhiteSpace(detail.CurrentOwnerTeamName)
            ? "Environment Group Owner Team Leader 或 Admin（整張審核）"
            : $"{detail.CurrentOwnerTeamName} Team Leader 或 Admin（整張審核）";
        return detail;
    }

    private static ReservationItemDto MapReviewItem(
        ReservationItem item,
        IReadOnlyDictionary<string, ApparatusCustodianProfile> custodianProfiles)
    {
        var dto = MapItem(item);
        dto.CurrentCustodianAccount = Clean(item.Apparatus.CustodianAccount);
        dto.CurrentCustodianName = Clean(ApparatusCustodianResolver.GetDisplayName(
            custodianProfiles,
            item.Apparatus.CustodianAccount));
        dto.CurrentOwnerTeamOptionId = item.Apparatus.OwnerTeamOptionId;
        dto.CurrentOwnerTeamName = item.Apparatus.OwnerTeamOption?.Name;
        return dto;
    }

    private static string FormatCapability(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : $" / {value}";

    private static ReservationDetailDto MapDetail(Reservation x) => new()
    {
        Id = x.Id,
        ReservationNo = x.ReservationNo,
        ApplicantAccount = x.ApplicantAccount,
        ApplicantName = x.ApplicantName,
        ApplicantDepartment = x.ApplicantDepartment,
        ApplicantEmail = x.ApplicantEmail,
        ApplicantExtension = x.ApplicantExtension,
        ApplicantAgentName = x.ApplicantAgentName,
        ApplicantAgentExtension = x.ApplicantAgentExtension,
        ApplicantAgentEmail = x.ApplicantAgentEmail,
        Purpose = x.Purpose,
        ProductModelName = x.ProductModelName,
        Customer = x.Customer,
        ProjectSubPu = x.ProjectSubPu,
        Note = x.Note,
        StartTime = x.StartTime,
        EndTime = x.EndTime,
        Status = x.Status,
        Mode = IsEnvironmentReservation(x) ? ReservationMode.Environment : ReservationMode.Direct,
        TestExecutionProfileId = x.TestExecutionProfileId,
        TestEnvironmentId = x.TestEnvironmentId,
        EquipmentGroupId = x.EquipmentGroupId,
        TestEnvironmentCodeSnapshot = x.TestEnvironmentCodeSnapshot,
        TestEnvironmentNameSnapshot = x.TestEnvironmentNameSnapshot,
        EquipmentGroupCodeSnapshot = x.EquipmentGroupCodeSnapshot,
        EquipmentGroupNameSnapshot = x.EquipmentGroupNameSnapshot,
        TestExecutionProfileCodeSnapshot = x.TestExecutionProfileCodeSnapshot,
        TestExecutionProfileNameSnapshot = x.TestExecutionProfileNameSnapshot,
        CreatedAt = x.CreatedAt,
        UpdatedAt = x.UpdatedAt,
        ApprovedAt = x.ApprovedAt,
        ApprovedBy = x.ApprovedBy,
        RejectedAt = x.RejectedAt,
        RejectedBy = x.RejectedBy,
        RejectReason = x.RejectReason,
        CancelledAt = x.CancelledAt,
        CancelledBy = x.CancelledBy,
        CancelReason = x.CancelReason,
        BorrowedAt = x.BorrowedAt,
        BorrowedBy = x.BorrowedBy,
        ReturnedAt = x.ReturnedAt,
        ReturnedBy = x.ReturnedBy,
        IsOverdue = (x.Status == ReservationStatus.Approved || x.Status == ReservationStatus.Borrowed)
            && x.EndTime < DateTime.UtcNow,
        Items = x.Items.OrderBy(i => i.ApparatusId).Select(MapItem).ToList(),
        ExtensionRequests = x.ExtensionRequests.OrderByDescending(e => e.RequestedAt)
            .Select(e => MapExtension(e, x)).ToList(),
        AuditEvents = x.AuditEvents.OrderBy(e => e.OccurredAt).ThenBy(e => e.Id)
            .Select(e => new ReservationAuditEventDto
            {
                Id = e.Id, Action = e.Action, FromStatus = e.FromStatus, ToStatus = e.ToStatus,
                ActorAccount = e.ActorAccount, ActorName = e.ActorName, OccurredAt = e.OccurredAt,
                Reason = e.Reason, Details = e.Details
            }).ToList()
    };

    private static ReservationItemDto MapItem(ReservationItem item) => new()
    {
        Id = item.Id,
        ApparatusId = item.ApparatusId,
        ApparatusName = item.ApparatusName,
        ProductsId = item.ProductsId,
        Kind = item.Kind,
        Brand = item.Brand,
        Model = item.Model,
        Number = item.Number,
        Place = item.Place,
        Custodian = item.Custodian,
        CustodianDepartment = item.CustodianDepartment,
        PriceUse = item.PriceUse,
        EquipmentGroupRequirementId = item.EquipmentGroupRequirementId,
        RequirementResourceTypeSnapshot = item.RequirementResourceTypeSnapshot,
        RequirementCapabilityTagSnapshot = item.RequirementCapabilityTagSnapshot
    };

    private static ReservationEnvironmentGroupDto MapEnvironmentGroup(EquipmentGroup group)
    {
        var devices = group.Devices.OrderByDescending(x => x.IsInEnvironment)
            .ThenBy(x => x.Apparatus.ProductsId)
            .ThenBy(x => x.Apparatus.Name)
            .ToList();
        var total = devices.Count;
        var present = devices.Count(x => x.IsInEnvironment);
        var completeness = EnvironmentGroupDeviceRules.GetCompletenessStatus(total, present);
        var missingDevices = devices.Where(x => !x.IsInEnvironment).ToList();
        var unavailableDevices = devices.Where(x => !ApparatusReservationRules.IsBookable(x.Apparatus)).ToList();
        var blockingReasons = new List<string>();
        if (completeness == EquipmentGroupCompletenessStatus.Unconfigured)
            blockingReasons.Add("測試環境尚未設定設備。");
        else if (completeness == EquipmentGroupCompletenessStatus.Incomplete)
            blockingReasons.Add($"測試環境設備不齊全：{string.Join("、", missingDevices.Select(x => DeviceLabel(x.Apparatus)))}。");
        if (unavailableDevices.Count != 0)
            blockingReasons.Add($"設備目前狀態不可使用：{string.Join("、", unavailableDevices.Select(x => DeviceLabel(x.Apparatus)))}。");

        return new ReservationEnvironmentGroupDto
        {
            Id = group.Id,
            Code = group.Code,
            Name = group.Name,
            OwnerTeamOptionId = group.OwnerTeamOptionId,
            OwnerTeamName = group.OwnerTeamOption?.Name ?? string.Empty,
            Site = group.Site,
            Status = group.Status,
            TotalDeviceCount = total,
            InEnvironmentDeviceCount = present,
            CompletenessStatus = completeness,
            MissingDevices = missingDevices.Select(x => new EquipmentGroupMissingDeviceDto
            {
                ApparatusId = x.ApparatusId,
                ApparatusName = x.Apparatus.Name,
                ProductsId = x.Apparatus.ProductsId
            }).ToList(),
            Devices = devices.Select(x => new ReservationEnvironmentGroupDeviceDto
            {
                ApparatusId = x.ApparatusId,
                ProductsId = x.Apparatus.ProductsId,
                Name = x.Apparatus.Name,
                Kind = x.Apparatus.Kind,
                Brand = x.Apparatus.Brand,
                Model = x.Apparatus.Model,
                ReservationStatus = x.Apparatus.ReservationStatus,
                IsInEnvironment = x.IsInEnvironment
            }).ToList(),
            CanReserve = group.Status == EquipmentGroupStatus.Active
                && completeness == EquipmentGroupCompletenessStatus.Complete
                && unavailableDevices.Count == 0,
            BlockingReasons = blockingReasons
        };
    }

    private void AddAudit(Reservation reservation, ReservationAuditEvent audit)
    {
        reservation.AuditEvents.Add(audit);
        _db.ReservationAuditEvents.Add(audit);
    }

    private static ReservationAuditEvent NewAudit(
        Guid reservationId,
        string action,
        ReservationStatus? fromStatus,
        ReservationStatus? toStatus,
        string actorAccount,
        string actorName,
        DateTime occurredAt,
        string? reason = null,
        string? details = null) => new()
        {
            Id = Guid.NewGuid(), ReservationId = reservationId, Action = action,
            FromStatus = fromStatus, ToStatus = toStatus, ActorAccount = actorAccount,
            ActorName = actorName, OccurredAt = occurredAt,
            Reason = Clean(reason), Details = Clean(details)
        };

    private static ReservationExtensionRequestDto MapExtension(
        ReservationExtensionRequest x,
        Reservation reservation) => new()
        {
            Id = x.Id, ReservationId = x.ReservationId, ReservationNo = reservation.ReservationNo,
            CurrentEndTimeSnapshot = x.CurrentEndTimeSnapshot, RequestedEndTime = x.RequestedEndTime,
            RequestedByAccount = x.RequestedByAccount, RequestedByName = x.RequestedByName,
            RequestedAt = x.RequestedAt, Status = x.Status, ReviewedAt = x.ReviewedAt,
            ReviewedByAccount = x.ReviewedByAccount, ReviewedByName = x.ReviewedByName,
            RejectReason = x.RejectReason, ApplicantDepartment = reservation.ApplicantDepartment,
            ApplicantExtension = reservation.ApplicantExtension,
            ApparatusNames = reservation.Items.OrderBy(i => i.ApparatusId).Select(i => i.ApparatusName).ToList(),
            Mode = IsEnvironmentReservation(reservation) ? ReservationMode.Environment : ReservationMode.Direct,
            EquipmentGroupId = reservation.EquipmentGroupId,
            EquipmentGroupName = reservation.EquipmentGroupNameSnapshot
        };

    private static ReservationExtensionRequestDto MapExtension(
        ReservationExtensionRequest extension,
        Reservation reservation,
        ReviewAccess access,
        ReservationExtensionReviewScope scope,
        IReadOnlyDictionary<string, ApparatusCustodianProfile> custodianProfiles)
    {
        var dto = MapExtension(extension, reservation);
        if (dto.Mode == ReservationMode.Environment)
        {
            dto.OwnerTeamOptionId = reservation.EquipmentGroup?.OwnerTeamOptionId;
            dto.OwnerTeamName = reservation.EquipmentGroup?.OwnerTeamOption?.Name;
            var canReview = extension.Status == ReservationExtensionRequestStatus.Pending
                && (access.IsAdmin || (scope == ReservationExtensionReviewScope.Team
                    && reservation.EquipmentGroup is not null
                    && access.LeaderTeamIds.Contains(reservation.EquipmentGroup.OwnerTeamOptionId)));
            dto.CanApprove = canReview;
            dto.CanReject = canReview;
            return dto;
        }

        var apparatus = reservation.Items.SingleOrDefault()?.Apparatus;
        if (apparatus is null) return dto;
        dto.CustodianAccount = Clean(apparatus.CustodianAccount);
        dto.CustodianName = Clean(ApparatusCustodianResolver.GetDisplayName(
            custodianProfiles,
            apparatus.CustodianAccount));
        dto.OwnerTeamOptionId = apparatus.OwnerTeamOptionId;
        dto.OwnerTeamName = apparatus.OwnerTeamOption?.Name;
        var canReviewDirect = extension.Status == ReservationExtensionRequestStatus.Pending
            && IsWithinExtensionReviewScope(apparatus, access, scope);
        dto.CanApprove = canReviewDirect;
        dto.CanReject = canReviewDirect;
        return dto;
    }

    private sealed record PreparedReservation(
        ReservationEnvironmentContext? EnvironmentContext,
        List<ReservationItem> Items)
    {
        public string[] ApparatusIds => Items.Select(x => x.ApparatusId).OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    private sealed record ReviewAccess(
        string Account,
        string ActorName,
        bool IsAdmin,
        HashSet<Guid> LeaderTeamIds);

    private sealed record TeamScopeContext(
        List<ReservationTeamScopeOptionDto> Options,
        Guid? SelectedTeamOptionId,
        Guid[] TeamOptionIds);
}
