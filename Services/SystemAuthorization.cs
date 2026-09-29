namespace SIT.DepartmentSystem.Web.Services;

public static class SystemAuthorization
{
    public const string AccessScopeClaim = "access_scope";
    public const string DataAccessLevelClaim = "data_access_level";

    public static class Policies
    {
        public const string RdApplicant = "RdApplicant";
        public const string CsitStaff = "CsitStaff";
        public const string ReservationUser = "ReservationUser";
        public const string Administration = "Administration";
        public const string BusinessWrite = "BusinessWrite";
    }

    public static class AccessScopes
    {
        public const string RdApplicant = "RdApplicant";
        public const string CsitStaff = "CsitStaff";
    }

    public static class DataAccessLevels
    {
        public const string ReadOnly = "ReadOnly";
        public const string Editor = "Editor";
    }
}
