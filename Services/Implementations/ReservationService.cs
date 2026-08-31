using System.Data;
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
    private readonly AppDbContext _db;
    private readonly IApparatusAvailabilityService _availability;
    private readonly IApparatusResourceCapabilityService _resourceCapabilities;
    private readonly IReservationPolicyService _policy;

    public ReservationService(
        AppDbContext db,
        IApparatusAvailabilityService availability,
        IApparatusResourceCapabilityService resourceCapabilities,
        IReservationPolicyService policy)
    {
        _db = db;
        _availability = availability;
        _resourceCapabilities = resourceCapabilities;
        _policy = policy;
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

    public async Task<ReservationApplicationOptionsDto> GetApplicationOptionsAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        EnsureReservationUser(user);
        var applicant = await ResolveApplicantAsync(user, cancellationToken);
        var options = await _db.SystemOptions.AsNoTracking()
            .Where(x => x.IsEnabled
                && (x.Category == SystemOptionCategories.Customer || x.Category == SystemOptionCategories.SubPu))
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
                request.Mode, request.Items, request.TestExecutionProfileId, request.Selections, cancellationToken);
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
            var existingMode = entity.TestExecutionProfileId.HasValue ? ReservationMode.Environment : ReservationMode.Direct;
            if (request.Mode != existingMode)
                throw new InvalidOperationException("A Draft reservation cannot change between Direct and Environment mode.");

            var prepared = await PrepareRequestAsync(
                request.Mode, request.Items, request.TestExecutionProfileId, request.Selections, cancellationToken);
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

    public async Task<IReadOnlyList<ReservationListDto>> GetStaffListAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        EnsureScope(user, SystemAuthorization.AccessScopes.CsitStaff);
        return await MapList(_db.Reservations.AsNoTracking())
            .ToListAsync(cancellationToken);
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
                Mode = x.TestExecutionProfileId.HasValue ? ReservationMode.Environment : ReservationMode.Direct,
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
            var department = query.Department.Trim().ToLower();
            source = source.Where(x => x.ApplicantDepartment.ToLower().Contains(department));
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
                Mode = x.TestExecutionProfileId.HasValue ? ReservationMode.Environment : ReservationMode.Direct,
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

    public async Task<ReservationExtensionRequestDto> ApproveExtensionAsync(
        Guid extensionId, ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        EnsureScope(user, SystemAuthorization.AccessScopes.CsitStaff);
        var account = GetAccount(user);
        var actorName = GetActorName(user);
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            var request = await _db.ReservationExtensionRequests
                .FromSqlInterpolated($"SELECT * FROM reservation_extension_requests WHERE id = {extensionId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new KeyNotFoundException($"Extension request {extensionId} was not found.");
            var entity = await FindRequiredForUpdateAsync(request.ReservationId, cancellationToken);
            if (request.Status != ReservationExtensionRequestStatus.Pending)
                throw new InvalidOperationException("Only a Pending extension request can be approved.");
            if (entity.Status is not ReservationStatus.Approved and not ReservationStatus.Borrowed)
                throw new InvalidOperationException("The reservation is no longer eligible for extension.");
            if (entity.EndTime != request.CurrentEndTimeSnapshot)
                throw new InvalidOperationException("Reservation EndTime changed after this extension request was created.");
            await _policy.ValidateExtensionDurationAsync(entity.EndTime, request.RequestedEndTime, cancellationToken);
            var apparatusIds = entity.Items.Select(x => x.ApparatusId).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            await LockApparatusAsync(apparatusIds, cancellationToken);
            await LoadAndValidateApparatusAsync(apparatusIds, cancellationToken);
            await _availability.EnsureBookableAsync(apparatusIds, cancellationToken);
            await _availability.EnsureNoOverlapAsync(
                apparatusIds, entity.EndTime, request.RequestedEndTime, entity.Id, cancellationToken);
            await _policy.EnsureDepartmentQuotaAsync(
                entity.ApplicantDepartment, entity.EndTime, request.RequestedEndTime, apparatusIds.Length, entity.Id,
                acquireTransactionLock: true, cancellationToken: cancellationToken);
            var now = DateTime.UtcNow;
            entity.ExtendEndTime(request.RequestedEndTime, now);
            request.Approve(account, actorName, now);
            AddAudit(entity, NewAudit(entity.Id, ReservationAuditActions.ExtensionApproved,
                entity.Status, entity.Status, account, actorName, now,
                details: $"New end: {request.RequestedEndTime:O}"));
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

    public Task<ReservationExtensionRequestDto> RejectExtensionAsync(
        Guid extensionId, ClaimsPrincipal user, string? reason, CancellationToken cancellationToken = default) =>
        ProcessExtensionAsync(extensionId, user, true, reason, cancellationToken);

    public Task<ReservationExtensionRequestDto> CancelExtensionAsync(
        Guid extensionId, ClaimsPrincipal user, CancellationToken cancellationToken = default) =>
        ProcessExtensionAsync(extensionId, user, false, null, cancellationToken);

    private async Task<ReservationExtensionRequestDto> ProcessExtensionAsync(
        Guid extensionId, ClaimsPrincipal user, bool reject, string? reason, CancellationToken cancellationToken)
    {
        if (reject) EnsureScope(user, SystemAuthorization.AccessScopes.CsitStaff); else EnsureReservationUser(user);
        var account = GetAccount(user);
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            var request = await _db.ReservationExtensionRequests
                .FromSqlInterpolated($"SELECT * FROM reservation_extension_requests WHERE id = {extensionId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new KeyNotFoundException($"Extension request {extensionId} was not found.");
            var entity = await FindRequiredForUpdateAsync(request.ReservationId, cancellationToken);
            if (!reject) EnsureOwner(entity, account);
            var now = DateTime.UtcNow;
            var actorName = reject ? GetActorName(user) : entity.ApplicantName;
            if (reject) request.Reject(account, actorName, Require(reason, nameof(reason)), now); else request.Cancel(now);
            AddAudit(entity, NewAudit(entity.Id,
                reject ? ReservationAuditActions.ExtensionRejected : ReservationAuditActions.ExtensionCancelled,
                entity.Status, entity.Status, account, actorName, now, reason));
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

    public async Task<IReadOnlyList<ReservationExtensionRequestDto>> GetPendingExtensionsAsync(
        ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        EnsureScope(user, SystemAuthorization.AccessScopes.CsitStaff);
        var items = await _db.ReservationExtensionRequests.AsNoTracking()
            .Include(x => x.Reservation).ThenInclude(x => x.Items)
            .Where(x => x.Status == ReservationExtensionRequestStatus.Pending)
            .OrderBy(x => x.RequestedAt)
            .ToListAsync(cancellationToken);
        return items.Select(x => MapExtension(x, x.Reservation)).ToList();
    }

    public async Task<ReservationOverdueResponseDto> GetOverdueAsync(
        ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        EnsureScope(user, SystemAuthorization.AccessScopes.CsitStaff);
        var account = GetAccount(user);
        var isAdmin = user.IsInRole("Admin");
        var now = DateTime.UtcNow;
        var leaderTeamIds = isAdmin
            ? []
            : await _db.TeamRoutings.AsNoTracking()
                .Where(x => x.IsEnabled && x.LeaderAccount.ToLower() == account)
                .Select(x => x.TeamOptionId)
                .ToListAsync(cancellationToken);

        var source = _db.Reservations.AsNoTracking()
            .Where(x => (x.Status == ReservationStatus.Borrowed || x.Status == ReservationStatus.Approved)
                && x.EndTime < now);

        if (!isAdmin)
        {
            source = source.Where(x => x.Items.Any(i =>
                (i.Apparatus.CustodianAccount != null
                    && i.Apparatus.CustodianAccount.ToLower() == account)
                || (i.Apparatus.OwnerTeamOptionId.HasValue
                    && leaderTeamIds.Contains(i.Apparatus.OwnerTeamOptionId.Value))));
        }

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
                    .Where(i => isAdmin
                        || (i.Apparatus.CustodianAccount != null
                            && i.Apparatus.CustodianAccount.ToLower() == account)
                        || (i.Apparatus.OwnerTeamOptionId.HasValue
                            && leaderTeamIds.Contains(i.Apparatus.OwnerTeamOptionId.Value)))
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
                        Custodian = i.Apparatus.Custodian,
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
            ValidateTimeRange(entity.StartTime, entity.EndTime);
            Require(entity.ApplicantExtension, nameof(entity.ApplicantExtension));
            await _policy.ValidateInitialDurationAsync(entity.StartTime, entity.EndTime, cancellationToken);
            await EnsureStoredEnvironmentSelectionsValidAsync(entity, cancellationToken);
            var apparatusIds = entity.Items.Select(x => x.ApparatusId).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (apparatusIds.Length == 0) throw new InvalidOperationException("A reservation must contain at least one apparatus.");
            await LockApparatusAsync(apparatusIds, cancellationToken);
            await LoadAndValidateApparatusAsync(apparatusIds, cancellationToken);
            await _availability.EnsureBookableAsync(apparatusIds, cancellationToken);
            await _availability.EnsureNoOverlapAsync(apparatusIds, entity.StartTime, entity.EndTime, entity.Id, cancellationToken);
            await _policy.EnsureDepartmentQuotaAsync(
                entity.ApplicantDepartment, entity.StartTime, entity.EndTime, apparatusIds.Length, entity.Id,
                acquireTransactionLock: true, cancellationToken: cancellationToken);
            await EnsureApplicationOptionIsActiveAsync(
                entity.Customer, SystemOptionCategories.Customer, nameof(entity.Customer), cancellationToken);
            await EnsureApplicationOptionIsActiveAsync(
                entity.ProjectSubPu, SystemOptionCategories.SubPu, nameof(entity.ProjectSubPu), cancellationToken);
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

    public Task<ReservationDetailDto> ApproveAsync(Guid id, ClaimsPrincipal user, CancellationToken cancellationToken = default) =>
        TransitionStaffAsync(id, user, ReservationAuditActions.Approved, null, (x, account, now) => x.Approve(account, now), cancellationToken);

    public Task<ReservationDetailDto> RejectAsync(Guid id, ClaimsPrincipal user, string? reason, CancellationToken cancellationToken = default) =>
        TransitionStaffAsync(id, user, ReservationAuditActions.Rejected, reason, (x, account, now) => x.Reject(account, reason, now), cancellationToken);

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

    private async Task<PreparedReservation> PrepareRequestAsync(
        ReservationMode mode,
        IReadOnlyCollection<ReservationItemRequest>? directItems,
        Guid? profileId,
        IReadOnlyCollection<ReservationRequirementSelectionRequest>? selections,
        CancellationToken cancellationToken)
    {
        if (mode == ReservationMode.Direct)
        {
            if (profileId.HasValue || selections?.Count > 0)
                throw new InvalidOperationException("Direct reservations cannot include environment selections.");
            var ids = NormalizeApparatusIds(directItems);
            await LockApparatusAsync(ids, cancellationToken);
            var directApparatuses = await LoadAndValidateApparatusAsync(ids, cancellationToken);
            await _availability.EnsureBookableAsync(ids, cancellationToken);
            return new PreparedReservation(null, directApparatuses.Select(x => ToReservationItem(x, null)).ToList());
        }

        if (mode != ReservationMode.Environment)
            throw new InvalidOperationException("Reservation mode is invalid.");
        if (!profileId.HasValue || profileId == Guid.Empty)
            throw new InvalidOperationException("TestExecutionProfileId is required for an Environment reservation.");
        if (directItems?.Count > 0)
            throw new InvalidOperationException("Environment reservations must use requirement selections.");

        var profile = await _db.TestExecutionProfiles.AsNoTracking()
            .Include(x => x.TestEnvironment)
            .Include(x => x.EquipmentGroup).ThenInclude(x => x.Requirements)
            .SingleOrDefaultAsync(x => x.Id == profileId.Value, cancellationToken)
            ?? throw new InvalidOperationException("Test execution profile does not exist.");
        if (profile.Status != TestExecutionProfileStatus.Active)
            throw new InvalidOperationException("Test execution profile is not Active.");
        if (profile.TestEnvironment.Status != TestEnvironmentStatus.Active)
            throw new InvalidOperationException("Test environment is not Active.");
        if (profile.EquipmentGroup.Status != EquipmentGroupStatus.Active)
            throw new InvalidOperationException("Equipment group is not Active.");

        var requirements = profile.EquipmentGroup.Requirements.ToDictionary(x => x.Id);
        var supplied = selections ?? [];
        if (supplied.GroupBy(x => x.EquipmentGroupRequirementId).Any(x => x.Count() > 1))
            throw new InvalidOperationException("Each equipment group requirement may appear only once.");
        var invalidRequirement = supplied.FirstOrDefault(x => !requirements.ContainsKey(x.EquipmentGroupRequirementId));
        if (invalidRequirement is not null)
            throw new InvalidOperationException("A selected requirement does not belong to the profile's equipment group.");

        var selectedByRequirement = supplied.ToDictionary(
            x => x.EquipmentGroupRequirementId,
            x => (IReadOnlyList<string>)(x.ApparatusIds ?? []).Select(id => Require(id, nameof(x.ApparatusIds))).ToList());

        foreach (var requirement in requirements.Values)
        {
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

            if (selectedByRequirement.TryGetValue(requirement.Id, out var selectedIds) && selectedIds.Count != 0)
            {
                var matchingIds = await _resourceCapabilities.GetMatchingApparatusIdsAsync(
                    requirement.ResourceType,
                    requirement.CapabilityTag,
                    selectedIds,
                    cancellationToken);
                var mismatched = selectedIds.Where(x => !matchingIds.Contains(x)).Distinct(StringComparer.Ordinal).ToArray();
                if (mismatched.Length != 0)
                    throw new InvalidOperationException(
                        $"Selected apparatus does not match requirement {requirement.ResourceType}{FormatCapability(requirement.CapabilityTag)}: {string.Join(", ", mismatched)}.");
            }
        }
        var preparedSelections = new List<(EquipmentGroupRequirement Requirement, string ApparatusId)>();
        foreach (var requirement in requirements.Values.OrderBy(x => x.Id))
        {
            selectedByRequirement.TryGetValue(requirement.Id, out var selected);
            selected ??= [];
            if (selected.Distinct(StringComparer.Ordinal).Count() != selected.Count)
                throw new InvalidOperationException($"Requirement {requirement.ResourceType} contains duplicate apparatus.");
            if (requirement.Required && selected.Count != requirement.Quantity)
                throw new InvalidOperationException($"Required requirement {requirement.ResourceType} must select exactly {requirement.Quantity} apparatus.");
            if (!requirement.Required && selected.Count != 0 && selected.Count != requirement.Quantity)
                throw new InvalidOperationException($"Optional requirement {requirement.ResourceType} must select either zero or exactly {requirement.Quantity} apparatus.");
            if (!requirement.AllowAlternative)
            {
                if (string.IsNullOrWhiteSpace(requirement.PreferredEquipmentId))
                    throw new InvalidOperationException($"Catalog configuration error: {requirement.ResourceType} disallows alternatives but has no preferred equipment.");
                if (requirement.Quantity != 1)
                    throw new InvalidOperationException($"Catalog configuration error: {requirement.ResourceType} disallows alternatives but quantity is not one.");
                if (selected.Count == 1 && !string.Equals(selected[0], requirement.PreferredEquipmentId, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Requirement {requirement.ResourceType} must use its preferred equipment.");
            }
            preparedSelections.AddRange(selected.Select(id => (requirement, id)));
        }

        var apparatusIds = preparedSelections.Select(x => x.ApparatusId).ToArray();
        if (apparatusIds.Length == 0)
            throw new InvalidOperationException("At least one apparatus is required.");
        if (apparatusIds.Distinct(StringComparer.Ordinal).Count() != apparatusIds.Length)
            throw new InvalidOperationException("The same apparatus cannot satisfy more than one requirement.");
        apparatusIds = apparatusIds.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        await LockApparatusAsync(apparatusIds, cancellationToken);
        var apparatuses = await LoadAndValidateApparatusAsync(apparatusIds, cancellationToken);
        await _availability.EnsureBookableAsync(apparatusIds, cancellationToken);
        var apparatusById = apparatuses.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var items = preparedSelections
            .Select(x => ToReservationItem(apparatusById[x.ApparatusId], x.Requirement))
            .ToList();
        var context = new ReservationEnvironmentContext(
            profile.Id,
            profile.TestEnvironmentId,
            profile.EquipmentGroupId,
            profile.TestEnvironment.Code,
            profile.TestEnvironment.Name,
            profile.EquipmentGroup.Code,
            profile.EquipmentGroup.Name,
            profile.Code,
            profile.Name);
        return new PreparedReservation(context, items);
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

    private static ReservationItem ToReservationItem(Apparatus x, EquipmentGroupRequirement? requirement) => new()
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
        Custodian = Clean(x.Custodian),
        CustodianDepartment = x.CustodianDepartment,
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
        Mode = x.TestExecutionProfileId.HasValue ? ReservationMode.Environment : ReservationMode.Direct,
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
        Items = x.Items.OrderBy(i => i.ApparatusId).Select(i => new ReservationItemDto
        {
            Id = i.Id,
            ApparatusId = i.ApparatusId,
            ApparatusName = i.ApparatusName,
            ProductsId = i.ProductsId,
            Kind = i.Kind,
            Brand = i.Brand,
            Model = i.Model,
            Number = i.Number,
            Place = i.Place,
            Custodian = i.Custodian,
            CustodianDepartment = i.CustodianDepartment,
            PriceUse = i.PriceUse,
            EquipmentGroupRequirementId = i.EquipmentGroupRequirementId,
            RequirementResourceTypeSnapshot = i.RequirementResourceTypeSnapshot,
            RequirementCapabilityTagSnapshot = i.RequirementCapabilityTagSnapshot
        }).ToList(),
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
            ApparatusNames = reservation.Items.OrderBy(i => i.ApparatusId).Select(i => i.ApparatusName).ToList()
        };

    private sealed record PreparedReservation(
        ReservationEnvironmentContext? EnvironmentContext,
        List<ReservationItem> Items)
    {
        public string[] ApparatusIds => Items.Select(x => x.ApparatusId).OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }
}
