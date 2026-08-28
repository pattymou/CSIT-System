namespace SIT.DepartmentSystem.Web.Services;

public static class SystemAuthorization
{
    public const string AccessScopeClaim = "access_scope";

    public static class Policies
    {
        public const string RdApplicant = "RdApplicant";
        public const string CsitStaff = "CsitStaff";
        public const string ReservationUser = "ReservationUser";
    }

    public static class AccessScopes
    {
        public const string RdApplicant = "RdApplicant";
        public const string CsitStaff = "CsitStaff";
    }
}
