using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using FBC.Domain;
using FBC.Web.Attributes;
using FBC.Web.Services;
using System.Text.RegularExpressions;

namespace FBC.Web.Controllers
{
	[ApiController]
	[Route("api/[controller]")]
	public class UserController : ControllerBase
	{
		private readonly ILogger<UserController> logger;
		private readonly IConfiguration configuration;
		private readonly FacebookWorkplaceService workplaceService;
		private readonly Context context;
		private readonly IMapper mapper;
		private readonly ActiveDirectoryService activeDirectoryService;
		private readonly string azureAdEmailSuffix;
		private readonly EmailService emailService;
		private const string UNAUTHORIZED_MESSAGE = "You are not allowed to access this application. Please contact your administrator to request access.";
		private readonly char[] chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz!@#$%^&*()_-+=[{]};:>|./?".ToCharArray();
		private const string EMAIL_REGEX = @"^([a-zA-Z0-9_\-\.]+)@([a-zA-Z0-9_\-\.]+)\.([a-zA-Z]{2,5})$";

		public UserController(ILogger<UserController> logger, IConfiguration configuration, FacebookWorkplaceService workplaceService, Context context, IMapper mapper, ActiveDirectoryService activeDirectoryService, EmailService emailService)
		{
			this.logger = logger;
			this.configuration = configuration;
			this.workplaceService = workplaceService;
			this.context = context;
			this.mapper = mapper;
			this.activeDirectoryService = activeDirectoryService;
			this.azureAdEmailSuffix = "@" + this.configuration["AzureAd:Domain"];
			this.emailService = emailService;
		}

		#region HTTP POST
		[HttpPost("login")]
		public async Task<ActionResult<DTO.Responses.User>> LoginAsync([FromBody] DTO.Requests.Login credentials)
		{
			var user = await context.Users
				.Include(u => u.Branches)
				.ThenInclude(x => x.Branch)
				.FirstOrDefaultAsync(x => x.UserName == credentials.Username && x.Type == UserType.NonAffiliates && x.IsActive && !x.Deleted); // x.Type == 3

			if (user == null || !user.VerifyPassword(credentials.Password))
			{
				return Unauthorized(new { message = "Invalid username or password." });
			}

			if (!user.RFPAccess)
			{
				return Unauthorized(new { message = UNAUTHORIZED_MESSAGE });
			}

			List<DTO.Responses.UserBranch> userBranches;

			if (credentials.Username == "admin")
			{
				userBranches = await context.Branches
					.Where(b => !b.Deleted)
					.Select(b => new DTO.Responses.UserBranch()
					{
						BranchId = b.Id,
						BranchName = b.Name
					})
					.OrderBy(b => b.BranchName)
					.ToListAsync();
			}
			else
			{
				userBranches = user.Branches
					.Where(b => !b.Branch.Deleted)
					.Select(b => new DTO.Responses.UserBranch()
					{
						BranchId = b.BranchId,
						BranchName = b.Branch.Name
					})
					.OrderBy(b => b.BranchName)
					.ToList();
			}

			return Ok(new DTO.Responses.User
			{
				Id = user.Id,
				Token = GenerateJwtToken(user.Id),
				Branches = userBranches,
				ContactEmail = user.ContactEmail,
				Locale = "en_US",
				Workplace = false
			});
		}

		[HttpPost("login/facebook")]
		public async Task<ActionResult<DTO.Responses.User>> LoginViaFacebookAsync([FromBody] DTO.Requests.Login credentials)
		{
			
			if (!VerifySignedRequest(credentials.Password, credentials.BotSource))
			{
				return Unauthorized(new { message = UNAUTHORIZED_MESSAGE });
			}

			// Get FB user 
			var facebookUser = await workplaceService.GetFacebookUserById(credentials.Username);
			if (facebookUser == null)
			{
				return Unauthorized(new { message = UNAUTHORIZED_MESSAGE });
			}

			var fbcUser = await context.Users
				.FirstOrDefaultAsync(x => string.Equals(x.ContactEmail, facebookUser.Email, StringComparison.OrdinalIgnoreCase)
					&& (x.Type == UserType.FBCStaff || x.Type == UserType.NonFBCStaff) && x.IsActive && !x.Deleted); //(x.Type == 1 || x.Type == 2)

			if (fbcUser == null || (credentials.BotSource.Equals("rfp", StringComparison.OrdinalIgnoreCase) && !fbcUser.RFPAccess))
			{
				return Unauthorized(new { message = UNAUTHORIZED_MESSAGE });
			}

			// use the other bot (not restricted) to pull branches
			var userBranches = await GetUserBranchesFromFacebookUsernameAsync(credentials.Username);

			var newUserBranches = 0;

			foreach (var x in userBranches)
			{
				var userBranch = await context.UserBranches.FirstOrDefaultAsync(y => y.UserId == fbcUser.Id && y.BranchId == x.BranchId);

				if (userBranch == null)
				{
					context.UserBranches.Add(new UserBranch(fbcUser.Id, x.BranchId));
					newUserBranches++;
				}
			}

			if (newUserBranches > 0)
			{
				await context.SaveChangesAsync();
			}

			return Ok(new DTO.Responses.User
			{
				Id = credentials.Username,
				Token = GenerateJwtToken(credentials.Username),
				Branches = userBranches,
				ContactEmail = fbcUser.ContactEmail,
				Locale = facebookUser.Locale.ToLower().Contains("fr") ? facebookUser.Locale : "en_US",
				Workplace = true
			});
		}

		[Authorize]
		[ClaimsFilter]
		[HttpPost]
		public async Task<ActionResult<DTO.Responses.User>> CreateAsync(string userId, [FromBody] DTO.Requests.CreateUser body)
		{
			if (body.Type != (int) UserType.FBCStaff && body.Type != (int) UserType.NonFBCStaff && body.Type != (int) UserType.NonAffiliates) // (body.Type < 1 || body.Type > 3)
			{
				return BadRequest();
			}

			// creating only type 3 users from wordpress
			if (body.Type == (int) UserType.FBCStaff || body.Type == (int) UserType.NonFBCStaff) // (body.Type == 1 || body.Type == 2)
			{
				return Forbid();
			}

			User user;

			// create user
			user = new User(body.UserName, body.Password, body.ContactEmail, (UserType) body.Type, body.FirstName, body.LastName, body.IsActive, body.IsNotify, false, null, body.RFPAccess, body.CreatedBy);
			context.Users.Add(user);

			// create user roles
			if (body.RoleIds != null && body.RoleIds.Count > 0)
            {
				foreach (var roleId in body.RoleIds)
                {
					var userRole = new UserRole(user.Id, roleId, userId);
					context.UserRoles.Add(userRole);
                }
            }

			// create user branches
			if (body.BranchIds != null && body.BranchIds.Count > 0) {
				foreach (var branchId in body.BranchIds) 
				{
					var userBranch = new UserBranch(user.Id, new Guid(branchId), userId);
					context.UserBranches.Add(userBranch);
				}
			}

			await context.SaveChangesAsync();

			return Ok(mapper.Map<Domain.User, DTO.Responses.User>(user));
		}

		[HttpPost("forgot-password")]
		public async Task<ActionResult> ForgotPasswordAsync([FromBody] DTO.Requests.ForgotPassword body)
		{	
			// check email address is valid
			if (!Regex.IsMatch(body.Username, EMAIL_REGEX, RegexOptions.IgnoreCase))
			{
				return BadRequest();
			}

			// find user
			var user = await context.Users
				.FirstOrDefaultAsync(x => x.UserName == body.Username && x.IsActive && !x.Deleted);

			if (user == null)
			{
				return NotFound();
			}

			if (user.Type != UserType.NonAffiliates)
			{
				return Forbid();
			}

			// generate a new password
			var password = GeneratePassword(12); // generate 12 characters randomly from chars array as new password

			// reset the password for this user
			user.UpdatePassword(password, user.Id);
			await context.SaveChangesAsync();

			// send email to this user
			var parameters = new Dictionary<string, string>()
            {
                {
                    "Password", password
                },
                {
                    "Firstname", user.FirstName
                },
            };

			var template = context.EmailTemplates.FirstOrDefault(t => t.Key == "FORGOT_PASSWORD");

			await emailService.SendForgotPasswordEmailAsync(body.Username, parameters, template.Subject, template.Body); // body.Username

			return Ok();
		}

		[Authorize]
		[ClaimsFilter]
		[HttpPost("current-user/change-password")]
		public async Task<ActionResult> ChangePasswordAsync(string userId, [FromBody] DTO.Requests.ChangePassword body)
		{	
			// find user
			var user = await context.Users.FirstOrDefaultAsync(u => u.Id == userId && u.IsActive && !u.Deleted);

			if (user == null)
			{
				return NotFound();
			}

			if (user.Type != UserType.NonAffiliates)
			{
				return Forbid();
			}

			if (!user.VerifyPassword(body.OldPassword)) {
				return Unauthorized();
			}

			// update the password
			user.UpdatePassword(body.NewPassword, userId);

			await context.SaveChangesAsync();

			return Ok();
		}

		[Authorize]
		[ClaimsFilter]
		[HttpPost("{id}/password")]
		public async Task<ActionResult> UpdatePasswordAsync(string userId, Guid id, [FromBody] DTO.Requests.ChangePassword body)
		{
			var user = await context.Users.FirstOrDefaultAsync(u => u.Id == id.ToString() && u.Deleted == false);

			if (user == null)
			{
				return NotFound();
			}

			if (user.Type != UserType.NonAffiliates) // (user.Type != 3)
			{
				return Forbid();
			}

			user.UpdatePassword(body.NewPassword, userId);
			await context.SaveChangesAsync();

			return Ok();
		}

		[HttpPost("{id}")]
		public async Task<ActionResult<DTO.Responses.User>> UpdateAsync(
			string userId,
			Guid id,
			[FromBody] DTO.Requests.EditUser body)
		{
			var user = await context.Users
				.Include(u => u.Branches)
				.Include(u => u.Roles)
				.FirstOrDefaultAsync(u => u.Id == id.ToString() && u.Deleted == false);

			if (user == null)
			{
				return NotFound();
			}

			user.UpdateFirstName(body.FirstName, userId);
			user.UpdateLastName(body.LastName, userId);
			user.UpdateContactEmail(body.ContactEmail, userId);

			user.UpdateIsActive(body.IsActive, userId);
			user.UpdateIsNotify(body.IsNotify, userId);
			user.UpdateRFPAccess(body.RFPAccess, userId);

			if (user.Type == UserType.NonFBCStaff || user.Type == UserType.NonAffiliates) // (user.Type == 2 || user.Type == 3)
			{
				user.UpdateType((UserType) body.Type, userId);
			}

			// update list of userroles
			if (body.RoleIds != null)
			{
				var currentUserRoleIds = user.Roles.Select(ur => ur.RoleId);
				var newRoleIds = body.RoleIds.Except(currentUserRoleIds);
				var deletedRowIds = currentUserRoleIds.Except(body.RoleIds);

				if (newRoleIds.Count() > 0)
				{
					var newUserRoles = newRoleIds.Select(roleId => new UserRole(user.Id, roleId, userId));
					context.UserRoles.AddRange(newUserRoles);
				}

				if (deletedRowIds.Count() > 0)
				{
					var deletedUserRoles = await context.UserRoles
						.Where(ur => ur.UserId == user.Id && deletedRowIds.Contains(ur.RoleId))
						.ToListAsync();
					context.UserRoles.RemoveRange(deletedUserRoles);
				}
			}

			if (user.Type == UserType.NonAffiliates) // (user.Type == 3)
			{
				if (!string.IsNullOrWhiteSpace(body.Password))
				{
					user.UpdatePassword(body.Password, userId);
				}

				// update list of userbranches
				if (body.BranchIds != null)
				{
					var currentUserBranchIds = user.Branches.Select(ub => ub.BranchId);
					var newBranchIds = body.BranchIds.Except(currentUserBranchIds);
					var deletedBranchIds = currentUserBranchIds.Except(body.BranchIds);

					if (newBranchIds.Count() > 0)
					{
						var newUserBranches = newBranchIds.Select(branchId => new FBC.Domain.UserBranch(user.Id, branchId));
						context.UserBranches.AddRange(newUserBranches);
					}

					if (deletedBranchIds.Count() > 0)
					{
						var deletedUserBranches = await context.UserBranches
							.Where(ub => ub.UserId == user.Id && deletedBranchIds.Contains(ub.BranchId))
							.ToListAsync();
						context.UserBranches.RemoveRange(deletedUserBranches);
					}
				}
			}

			await context.SaveChangesAsync();

			return Ok(mapper.Map<Domain.User, DTO.Responses.User>(user));
		}
		#endregion

		#region HTTP DELETE
		[Authorize]
		[ClaimsFilter]
		[HttpDelete("{id}")]
		public async Task<ActionResult> DeleteUserAsync(
			string userId, Guid id,
			[FromQuery] string appSource = "bot")
		{
			var user = await context.Users.FirstOrDefaultAsync(d => d.Id == id.ToString() && d.Deleted == false);

			if (user == null)
			{
				return NotFound();
			}

			if (user.Type == UserType.FBCStaff) // (user.Type == 1)
			{
				return Forbid();
			}

			user.Delete(userId, appSource);

			if (user.Type == UserType.NonFBCStaff) // (user.Type == 2)
			{
				try
				{
					await activeDirectoryService.DisableAccount(user.ActiveDirectoryUserId);
				}
				catch (Exception) {}
			}

			await context.SaveChangesAsync();

			return Ok();
		}

		[Authorize]
		[ClaimsFilter]
		[HttpPost("bulk-delete")]
		public async Task<ActionResult> DeleteUsersAsync(
			string userId,
			[FromBody] Guid[] ids,
			[FromQuery] string appSource = "bot")
		{
			foreach (var id in ids)
			{
				var user = await context.Users.FirstOrDefaultAsync(d => d.Id == id.ToString());

				if (user != null)
				{
					user.Delete(userId, appSource);
				}
			}

			await context.SaveChangesAsync();

			return Ok();
		}

		#endregion

		#region HTTP GET
		[HttpGet("{id}")]
		public async Task<ActionResult<DTO.Responses.User>> GetUserAsync(string id, [FromQuery] string appSource = "bot")
		{
			var user = await context.Users
				.Include(u => u.Branches)
				.ThenInclude(ub => ub.Branch)
				.Include(u => u.Roles)
				.ThenInclude(ur => ur.Role)
				.FirstOrDefaultAsync(u => u.Id == id);

			if (user == null)
			{
				return NotFound();
			}

			// get the userbranches
			var userBranches = user.Branches
				.Where(ub => !ub.Branch.Deleted)
				.Select(ub => new DTO.Responses.UserBranch()
				{
					BranchId = ub.BranchId,
					BranchName = ub.Branch.Name
				})
				.ToList();

			// get the userroles
			var userRoles = user.Roles
				.Where(ur => !ur.Role.Deleted)
				.Select(ur => new DTO.Responses.UserRole()
				{
					RoleId = ur.RoleId,
					RoleName = ur.Role.Name
				})
				.ToList();

			var userResult = mapper.Map<Domain.User, DTO.Responses.User>(user);
			userResult.Branches = userBranches;
			userResult.Roles = userRoles;

			return Ok(userResult);
		}

		[HttpGet]
		public async Task<ActionResult<DTO.Responses.PagedEntity<DTO.Responses.User>>> GetAllUsersAsync(
			[FromQuery] bool includeDeleted = false,
			[FromQuery] string searchQuery = null,
			[FromQuery] string orderBy = null,
			[FromQuery] string order = null,
			[FromQuery] int? offset = null,
			[FromQuery] int? perPage = null,
			[FromQuery] bool? isActive = null,
			[FromQuery] bool? isNotify = null,
			[FromQuery] bool? isAffiliated = null,
			[FromQuery] bool? rfpAccess = null,
			[FromQuery] Guid? branchId = null,
			[FromQuery] string roleId = null,
			[FromQuery] bool trash = false
			)
		{

			IQueryable<Domain.User> queryable = context.Users
				.Where(u => u.UserName != "admin")
				.Include(u => u.Branches)
				.ThenInclude(ub => ub.Branch)
				.Include(u => u.Roles)
				.ThenInclude(ur => ur.Role);

			// trash includes only deleted, includeDeleted includes all
			if (!includeDeleted && trash)
			{
				// only get trash results for past 30 days
				var utcNowDate = DateTime.UtcNow.Date;
				queryable = queryable
					.Where(u => u.Deleted && u.DeleteDate.Value.Date >= utcNowDate.AddDays(-30));
			}
			else if (!includeDeleted && !trash)
			{
				queryable = queryable
					.Where(r => !r.Deleted);
			}

			// apply search query
			if (!string.IsNullOrWhiteSpace(searchQuery))
			{
				queryable = queryable
					.Where(d => d.UserName.Contains(searchQuery, StringComparison.OrdinalIgnoreCase) ||
					d.FirstName.Contains(searchQuery, StringComparison.OrdinalIgnoreCase) ||
					d.LastName.Contains(searchQuery, StringComparison.OrdinalIgnoreCase) ||
					d.ContactEmail.Contains(searchQuery, StringComparison.OrdinalIgnoreCase));
			}

			if (isActive != null)
			{
				if ((bool)isActive)
				{
					queryable = queryable.Where(t => t.IsActive);
				}
				else 
				{
					queryable = queryable.Where(t => !t.IsActive);
				}
			}

			if (isNotify != null)
			{
				if ((bool)isNotify)
				{
					queryable = queryable.Where(t => t.IsNotify);
				}
				else
				{
					queryable = queryable.Where(t => !t.IsNotify);
				}
			}

			if (isAffiliated != null)
			{
				if ((bool)isAffiliated)
				{
					queryable = queryable.Where(t => t.IsAffiliated);
				}
				else
				{
					queryable = queryable.Where(t => !t.IsAffiliated);
				}
			}

			if (rfpAccess != null)
			{
				if ((bool)rfpAccess)
				{
					queryable = queryable.Where(t => t.RFPAccess);
				}
				else
				{
					queryable = queryable.Where(t => !t.RFPAccess);
				}
			}

			if (branchId != null)
			{
				queryable = queryable.Where(u => u.Branches.Any(ub => ub.BranchId == branchId));
			}

			if (roleId != null)
			{
				queryable = queryable.Where(u => u.Roles.Any(ur => ur.RoleId == roleId));
			}

			// order results
			if (!string.IsNullOrWhiteSpace(orderBy) && !string.IsNullOrWhiteSpace(order))
			{
				if (order.Equals("asc", StringComparison.OrdinalIgnoreCase))
				{
					queryable = orderBy switch
					{
						"userName" => queryable.OrderBy(d => d.UserName),

						"contactEmail" => queryable.OrderBy(d => d.ContactEmail),

						"firstName" => queryable.OrderBy(d => d.FirstName),

						"lastName" => queryable.OrderBy(d => d.LastName),

						"isActive" => queryable.OrderBy(d => d.IsActive),

						"isNotify" => queryable.OrderBy(d => d.IsNotify),

						"isAffiliated" => queryable.OrderBy(d => d.IsAffiliated),

						"rfpAccess" => queryable.OrderBy(d => d.RFPAccess),

						"id" => queryable.OrderBy(d => d.Id),

						_ => queryable.OrderBy(d => d.UserName),
					};
				}
				else
				{
					queryable = orderBy switch
					{
						"userName" => queryable.OrderByDescending(d => d.UserName),

						"contactEmail" => queryable.OrderByDescending(d => d.ContactEmail),

						"firstName" => queryable.OrderByDescending(d => d.FirstName),

						"lastName" => queryable.OrderByDescending(d => d.LastName),

						"isActive" => queryable.OrderByDescending(d => d.IsActive),

						"isNotify" => queryable.OrderByDescending(d => d.IsNotify),

						"isAffiliated" => queryable.OrderByDescending(d => d.IsAffiliated),

						"rfpAccess" => queryable.OrderByDescending(d => d.RFPAccess),

						"id" => queryable.OrderByDescending(d => d.Id),

						_ => queryable.OrderByDescending(d => d.UserName),
					};
				}
			}

			var resultCount = queryable;
			var count = resultCount.Count();

			if (offset.HasValue && perPage.HasValue)
			{
				queryable = queryable
					.Skip(offset.Value)
					.Take(perPage.Value);
			}

			var result = queryable.Select(d => mapper.Map<Domain.User, DTO.Responses.User>(d)).ToList();
			var paged = new DTO.Responses.PagedEntity<DTO.Responses.User>()
			{
				TotalCount = count,
				Entities = result,
			};

			return Ok(paged);
		}

		[HttpGet("counts")]
		public async Task<ActionResult<DTO.Responses.EntityCounts>> GetUsersCountsAsync()
		{
			var utcNowDate = DateTime.UtcNow.Date;

			var usersCounts = new DTO.Responses.EntityCounts()
			{
				Total = await context.Users.CountAsync(d => !d.Deleted),
				// only get trash counts for past 30 days
				Deleted = await context.Users
					.CountAsync(d => d.Deleted && d.DeleteDate.Value.Date >= utcNowDate.AddDays(-30)),
			};

			return Ok(usersCounts);
		}
		#endregion

		#region HTTP PUT
		[Authorize]
		[ClaimsFilter]
		[HttpPut("bulk-activate")]
		public async Task<ActionResult> ActivateUsersAsync(string userId, [FromBody] string[] ids)
		{
			foreach (var id in ids)
			{
				var user = await context.Users.FindAsync(id);

				if (user != null)
				{
					user.UpdateIsActive(true, userId);
				}
			}

			await context.SaveChangesAsync();

			return Ok();
		}

		[Authorize]
		[ClaimsFilter]
		[HttpPut("bulk-deactivate")]
		public async Task<ActionResult> DeactivateUsersAsync(string userId, [FromBody] string[] ids)
		{
			foreach (var id in ids)
			{
				var user = await context.Users.FindAsync(id);

				if (user != null)
				{
					user.UpdateIsActive(false, userId);
				}
			}

			await context.SaveChangesAsync();

			return Ok();
		}

		[Authorize]
		[ClaimsFilter]
		[HttpPut("bulk-add-notification")]
		public async Task<ActionResult> AddUsersNotificationAsync(string userId, [FromBody] string[] ids)
		{
			foreach (var id in ids)
			{
				var user = await context.Users.FindAsync(id);

				if (user != null)
				{
					user.UpdateIsNotify(true, userId);
				}
			}

			await context.SaveChangesAsync();

			return Ok();
		}

		[Authorize]
		[ClaimsFilter]
		[HttpPut("bulk-remove-notification")]
		public async Task<ActionResult> RemoveUsersNotificationAsync(string userId, [FromBody] string[] ids)
		{
			foreach (var id in ids)
			{
				var user = await context.Users.FindAsync(id);

				if (user != null)
				{
					user.UpdateIsNotify(false, userId);
				}
			}

			await context.SaveChangesAsync();

			return Ok();
		}

		[Authorize]
		[ClaimsFilter]
		[HttpPut("bulk-mark-affiliated")]
		public async Task<ActionResult> MarkUsersAffiliated(string userId, [FromBody] string[] ids)
		{
			foreach (var id in ids)
			{
				var user = await context.Users.FindAsync(id);

				if (user != null)
				{
					user.UpdateIsAffiliated(true, userId);
				}
			}

			await context.SaveChangesAsync();

			return Ok();
		}

		[Authorize]
		[ClaimsFilter]
		[HttpPut("bulk-remove-affiliation")]
		public async Task<ActionResult> RemoveUsersAffiliationAsync(string userId, [FromBody] string[] ids)
		{
			foreach (var id in ids)
			{
				var user = await context.Users.FindAsync(id);

				if (user != null)
				{
					user.UpdateIsAffiliated(false, userId);
				}
			}

			await context.SaveChangesAsync();

			return Ok();
		}

		[Authorize]
		[ClaimsFilter]
		[HttpPut("restore/{id}")]
		public async Task<ActionResult> RestoreUserAsync(string userId, string id)
		{
			var user = await context.Users.FindAsync(id);

			if (user == null)
			{
				return NotFound();
			}

			user.Restore(userId);

			await context.SaveChangesAsync();

			return Ok(mapper.Map<Domain.User, DTO.Responses.User>(user));
		}

		[Authorize]
		[ClaimsFilter]
		[HttpPut("bulk-restore")]
		public async Task<ActionResult> RestoreUsersAsync(string userId, [FromBody] string[] ids)
		{
			foreach (var id in ids)
			{
				var user = await context.Users.FindAsync(id);

				if (user != null)
				{
					user.Restore(userId);
				}
			}

			await context.SaveChangesAsync();

			return Ok();
		}

		[Authorize]
		[ClaimsFilter]
		[HttpPut("update-branch")]
		public async Task<ActionResult> UpdateUserBranchAsync(string userId, [FromBody] DTO.Requests.UserBranch userBranchBody)
		{
			var userBranch = await context.UserBranches
				.FirstOrDefaultAsync(ub => ub.UserId == userBranchBody.UserId);

			if (userBranch == null)
			{
				return NotFound();
			}

			userBranch.UpdateUserBranch(userBranchBody.BranchId, userId);

			await context.SaveChangesAsync();

			return Ok(mapper.Map<UserBranch, DTO.Responses.UserBranch>(userBranch));
		}
		#endregion

		#region Private Functions
		[NonAction]
		private string GenerateJwtToken(string userId)
		{
			var tokenHandler = new JwtSecurityTokenHandler();
			var key = Encoding.ASCII.GetBytes(configuration["Tokens:Key"]);

			var tokenDescriptor = new SecurityTokenDescriptor
			{
				Subject = new ClaimsIdentity(new Claim[]
				{
					new Claim(ClaimTypes.NameIdentifier, userId)
				}),
				Expires = DateTime.UtcNow.AddDays(double.Parse(configuration["Tokens:LifespanInDays"])),
				SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
			};

			var token = tokenHandler.CreateToken(tokenDescriptor);

			return new JwtSecurityTokenHandler().WriteToken(token);
		}

		[NonAction]
		private async Task<IList<DTO.Responses.UserBranch>> GetUserBranchesFromFacebookUsernameAsync(string username)
		{
			var groups = await workplaceService.GetUserGroups(username, 1000);

			var groupToBranch = await context.Branches
				.Where(b => b.FacebookGroupId != null && !b.Deleted)
				.ToDictionaryAsync(k => k.FacebookGroupId, v => new DTO.Responses.UserBranch { BranchId = v.Id, BranchName = v.Name });

			var userBranches = new List<DTO.Responses.UserBranch>();

			foreach (var group in groups)
			{
				if (groupToBranch.ContainsKey(group))
				{
					userBranches.Add(groupToBranch[group]);
				}
			}

			return userBranches.OrderBy(b => b.BranchName).ToList();
		}

		[NonAction]
		private bool VerifySignedRequest(string signed_request, string botSource = "rfp")
		{
			var parts = signed_request.Split('.');

			if (parts.Length != 2)
			{
				return false;
			}

			var signature = WebEncoders.Base64UrlDecode(parts[0]);

			using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(
				botSource.ToLower().Equals("rfp") ? configuration["FacebookRFPBot:AppSecret"] : configuration["FacebookInfoBot:AppSecret"])
			);

			var expectedSignature = hmac.ComputeHash(Encoding.UTF8.GetBytes(parts[1]));

			return signature.SequenceEqual(expectedSignature);
		}

		[NonAction]
		private string GeneratePassword(int length) {
			StringBuilder res = null;

			while (true) {
				res = new StringBuilder();

				byte[] randomNumbersInLength = new byte[sizeof(uint) * length]; // 4 bytes for one unsigned integer

				using (RNGCryptoServiceProvider rngService = new RNGCryptoServiceProvider()) {
					rngService.GetBytes(randomNumbersInLength);
				}

				for (int i = 0; i < length ; i++) {
					uint randomNum = BitConverter.ToUInt32(randomNumbersInLength, i * 4); // Returns a 32-bit unsigned integer converted from four bytes at a specified position in a byte array
					int index = (int) (randomNum % (uint) chars.Length);
					res.Append(chars[index]);
				}

				if (!IsDangerousPassword(res.ToString())) {
					break;
				}
			}

			return res.ToString();
        }

		[NonAction]
		private bool IsDangerousPassword(string password) { // based on .net reference source CrossSiteScriptingValidation.cs
            var dangerousChars = new char[] {'<', '&'};

			int i = 0;
			while (i < password.Length) {
				int n = password.IndexOfAny(dangerousChars, i);

				if (n < 0) { // not found
					return false;
				}

				if (n == password.Length -1) { // last char
					return false;
				}

				if (password[n] == '<') {
					if (Char.IsLetter(password[n]) || password[n+1] == '!' || password[n+1] == '/' || password[n+1] == '?') {
						return true;
					}
				} else if (password[n] == '&') {
					if (password[n+1] == '#') {
						return true;
					}
				}

				i = n + 1; // start from the char after password[n], since n is the first occurrence
			}

			return false;
        }
		#endregion
	}
}
