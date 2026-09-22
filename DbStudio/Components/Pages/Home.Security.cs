using DbStudio.Core;

namespace DbStudio.Components.Pages;

/// <summary>
/// 工作台的系统成员、角色和密码交互。
/// </summary>
public partial class Home
{
    /// <summary>
    /// 读取最新成员与角色后进入权限管理页面。
    /// </summary>
    private void Security()
    {
        menu = "";
        Run(() =>
        {
            roles = Store.Roles(principal);
            users = Store.Users(principal);
            view = "security";
        });
    }

    /// <summary>
    /// 初始化新增或编辑成员表单，不读取已有密码。
    /// </summary>
    private void EditUser(UserInfo? item = null)
    {
        editUserId = item?.Id ?? Guid.NewGuid().ToString("N");
        editLogin = item?.Login ?? "";
        editName = item?.DisplayName ?? "";
        editRole = item?.RoleId ?? "reader";
        editEnabled = item?.Enabled ?? true;
        editPassword = "";
        modal = "user";
    }

    /// <summary>
    /// 保存成员并刷新列表，旧会话失效规则由服务层处理。
    /// </summary>
    private void SaveUser() => Run(() =>
    {
        Store.SaveUser(principal, new(editUserId, editLogin, editName, editRole, editEnabled), editPassword);
        users = Store.Users(principal);
        modal = "";
        message = "用户已保存；已有会话需重新登录。";
    });

    /// <summary>
    /// 初始化角色权限编辑表单。
    /// </summary>
    private void EditRole(Role? item = null)
    {
        editRoleId = item?.Id ?? Guid.NewGuid().ToString("N");
        editRoleName = item?.Name ?? "";
        editPermissions = item?.Permissions ?? Permission.Read;
        modal = "role";
    }

    /// <summary>
    /// 增减角色的单个权限位。
    /// </summary>
    private void SetPermission(Permission permission, bool on)
    {
        if (on)
        {
            editPermissions |= permission;
        }
        else
        {
            editPermissions &= ~permission;
        }
    }

    /// <summary>
    /// 保存角色并刷新权限列表。
    /// </summary>
    private void SaveRole() => Run(() =>
    {
        Store.SaveRole(principal, new(editRoleId, editRoleName, editPermissions));
        roles = Store.Roles(principal);
        modal = "";
        message = "角色权限已更新，后续操作立即按新权限检查。";
    });

    /// <summary>
    /// 修改密码成功后回到登录页，要求建立新会话。
    /// </summary>
    private void ChangePassword() => Run(() =>
    {
        Store.ChangePassword(principal, oldPassword, newPassword);
        dirty = false;
        Navigation.NavigateTo("/login", true);
    });

    /// <summary>
    /// 将权限位转换为统一的中文名称。
    /// </summary>
    private static string PermissionName(Permission permission) => permission switch
    {
        Permission.Read => "查看设计",
        Permission.Design => "维护设计",
        Permission.Projects => "创建项目",
        Permission.Export => "导出与 SQL",
        Permission.Security => "用户与角色",
        _ => ""
    };

    private static readonly Permission[] PermissionList =
        [Permission.Read, Permission.Design, Permission.Projects, Permission.Export, Permission.Security];
}
