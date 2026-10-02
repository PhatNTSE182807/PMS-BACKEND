using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using N_Tier.Application.Models;
using N_Tier.Application.Models.User;
using N_Tier.Application.Services;

namespace N_Tier.API.Controllers;

public class UsersController(IUserService userService) : ApiController
{
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAllAsync()
    {
        return Ok(ApiResult<IEnumerable<UserResponseModel>>.Success(await userService.GetAllAsync()));
    }

    [HttpGet("{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetByIdAsync(string id)
    {
        return Ok(ApiResult<UserResponseModel>.Success(await userService.GetByIdAsync(id)));
    }

    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> RegisterAsync(CreateUserModel createUserModel)
    {
        return Ok(ApiResult<CreateUserResponseModel>.Success(await userService.CreateAsync(createUserModel)));
    }

    [HttpPost("authenticate")]
    [AllowAnonymous]
    public async Task<IActionResult> LoginAsync(LoginUserModel loginUserModel)
    {
        return Ok(ApiResult<LoginResponseModel>.Success(await userService.LoginAsync(loginUserModel)));
    }
}
