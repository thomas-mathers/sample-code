using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AutoMapper;
using BSC.OrderTool.Domain;
using BSC.OrderTool.Web.DTO;
using BSC.OrderTool.Web.Extensions;
using BSC.OrderTool.Web.Middleware;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace BSC.OrderTool.Web.Controllers
{
    [ApiExceptionFilter]
    [Produces("application/json")]
    [Route(template: "api/order-job")]
    public class OrderJobController : Controller
    {
        private readonly BscDbContext context;
        private readonly IMapper mapper;
        private readonly IConfiguration configuration;

        public OrderJobController(IConfiguration configuration, BscDbContext context, IMapper mapper) 
        {
            this.context = context;            
            this.mapper = mapper;
            this.configuration = configuration;
        }

        [ClaimsFilter]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [HttpGet("{id}")]
        public async Task<IActionResult> Get(Guid id)
        {
            var item = await context.OrderJobs
                .Include(t => t.Account)
                .Include(t => t.Site)
                .Include(t => t.CurrentResult)
                .ThenInclude(t => t.ScreenShots)
                .Where(t => !t.Deleted)
                .FirstOrDefaultAsync(x => x.Id == id);

            if (item == null)
            {
                return NotFound("id");
            }

            var job = mapper.Map<Domain.OrderJob, DTO.OrderJob>(item);

            return Ok(job);
        }

        [ClaimsFilter]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [HttpGet("{id}/results")]
        public async Task<IActionResult> GetResults(Guid id,
            [FromQuery] int pageIndex = 0,
            [FromQuery] int pageSize = 20)
        {
            var item = await context.OrderJobs
                .Where(t => !t.Deleted)
                .FirstOrDefaultAsync(x => x.Id == id);

            if (item == null)
            {
                return NotFound("id");
            }

            var items = context.OrderJobResults
                .Include(t => t.ScreenShots)
                .Where(t => !t.Deleted && t.OrderJobId == id)
                .OrderByDescending(t => t.LastModifiedDate);


            var count = items.Count();
            var results = await items.Skip(pageIndex * pageSize).Take(pageSize).ToListAsync();

            var data = new List<DTO.OrderJobResult>();
            foreach (var result in results)
            {
                var jobResult = mapper.Map<Domain.OrderJobResult, DTO.OrderJobResult>(result);
                data.Add(jobResult);
            }

            return Ok(new PaginatedResponse<DTO.OrderJobResult>(data, pageSize, pageIndex, count));
        }

        [ClaimsFilter]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [HttpGet]
        public async Task<IActionResult> Get(
            [FromQuery] int includeAll = 1,
            [FromQuery] int pageIndex = 0,
            [FromQuery] int pageSize = 1000)
        {
            var items = context.OrderJobs
                .Include(t => t.Account)
                .Include(t => t.Site)
                .Include(t => t.CurrentResult)
                .Where(t => !t.Deleted);

            if (includeAll == 0)
            {
                items = items.Where(t => t.IsActive);
            }

            items = items.OrderBy(t => t.SiteId);

            var count = items.Count();

            items = items.Skip(pageIndex * pageSize).Take(pageSize);

            var data = await items.Select(t => mapper.Map<Domain.OrderJob, DTO.OrderJob>(t)).ToListAsync();

            return Ok(new PaginatedResponse<DTO.OrderJob>(data, pageSize, pageIndex, count));
        }

        [HttpPost]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "admin")]
        [ClaimsFilter]
        public async Task<IActionResult> PostAsync(string userId, [FromBody] CreateOrderJob body)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var orderJob = new Domain.OrderJob(body.SiteId, body.AccountId, body.Schedule, null, body.IsActive, userId);

            context.OrderJobs.Add(orderJob);
            await context.SaveChangesAsync();

            return await Get(orderJob.Id);
        }


        [HttpPut]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "admin")]
        [ClaimsFilter]
        [Route("{id}")]
        public async Task<dynamic> Put(string userId, Guid id, [FromBody] DTO.OrderJob orderJob)
        {
            var item = await context.OrderJobs
                .Include(t => t.Account)
                .Include(t => t.Site)
                .Include(t => t.CurrentResult)
                .ThenInclude(t => t.ScreenShots)
                .Where(t => !t.Deleted)
                .FirstOrDefaultAsync(x => x.Id == id);

            if (item == null)
            {
                return NotFound("id");
            }

            item.AccountId = orderJob.AccountId;
            item.SiteId = orderJob.SiteId;
            item.Schedule = orderJob.Schedule;
            item.Url= orderJob.Url;
            item.IsActive = orderJob.IsActive;
            item.CleanupNextRun = orderJob.CleanupNextRun;

            item.Update(userId);

            await context.SaveChangesAsync();

            return await Get(id);
        }

        [HttpDelete]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "admin")]
        [ClaimsFilter]
        [Route("{id}")]
        public async Task<dynamic> Delete(string userId, Guid id)
        {
            var item = await context.OrderJobs
                .FirstOrDefaultAsync(t => t.Id == id && !t.Deleted);

            if (item == null)
            {
                return NotFound("id");
            }

            item.Delete(userId);
            await context.SaveChangesAsync();

            return Ok();
        }

        [HttpPost("{id}/process")]
        public async Task<IActionResult> PostAsync(Guid id)
        {
            var item = await context.OrderJobs
               .Include(t => t.Account)
               .Include(t => t.Site)
               .Include(t => t.CurrentResult)
               .Include(t => t.Results)
               .Where(t => !t.Deleted)
               .FirstOrDefaultAsync(x => x.Id == id);

            if (item == null)
            {
                return NotFound("id");
            }

            item.Queued();
            await context.SaveChangesAsync();

            QueueClient jobQueue;

            if (string.IsNullOrEmpty(item.Account.QueueName))
            {
                jobQueue = new QueueClient(configuration["ServiceBusConnectionString"], configuration["ServiceBusQueueName"]);
            }
            else
            {
                jobQueue = new QueueClient(configuration["ServiceBusConnectionString"], item.Account.QueueName);
            }

            // 0 = order
            // GUID ID
            // 0 = no retry
            var message = new Message(Encoding.UTF8.GetBytes("0 " + id.ToString() + " 0"));
            message.TimeToLive = new TimeSpan(4, 0, 0);
            await jobQueue.SendAsync(message);

            return Ok("ok");
        }
    }
}