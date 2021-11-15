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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace BSC.OrderTool.Web.Controllers
{
    [ApiExceptionFilter]
    [Produces("application/json")]
    [Route(template: "api/purchase-job")]
    public class PurchaseJobController : Controller
    {
        private readonly BscDbContext databaseContext;
        private readonly IMapper mapper;
        private readonly IConfiguration configuration;

        public PurchaseJobController(IConfiguration configuration, BscDbContext databaseContext, IMapper mapper) 
        {
            this.databaseContext = databaseContext;
            this.mapper = mapper;
            this.configuration = configuration;
        }

        [HttpGet]
        public async Task<IActionResult> Get(
            [FromQuery] string siteId,
            [FromQuery] Guid? accountId,
            [FromQuery] string status,
            [FromQuery] DateTime? dateFrom = null,
            [FromQuery] DateTime? dateTo = null,
            [FromQuery] string sortBy = "lastModifiedDate",
            [FromQuery] string sortDir = "desc",
            [FromQuery] int pageIndex = 0,
            [FromQuery] int pageSize = 100)
        {
            var items = databaseContext.PurchaseJobOrders
                .Include(t => t.Account)
                .Include(t => t.CreatedBy)
                .Include(t => t.Shipping)
                .Include(t => t.Payment)
                .Include(t => t.Affiliate)
                .Include(t => t.DiscountCodes)
                .Include(t => t.LineItems)
                .Include(t => t.PurchaseJob)
                .ThenInclude(t => t.Site)
                .Where(t => !t.Deleted);

            if (!string.IsNullOrWhiteSpace(siteId))
            {
                items = items.Where(t => t.PurchaseJob.SiteId == siteId);
            }
            if (accountId.HasValue)
            {
                items = items.Where(t => t.AccountId == accountId.Value);
            }
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (Enum.TryParse(status, out PurchaseJobStatus s))
                {
                    items = items.Where(t => t.Status == s);
                }
            }
            if (!dateFrom.HasValue)
            {
                dateFrom = DateTime.UtcNow.AddDays(-7);
            }
            if (!dateTo.HasValue)
            {
                dateTo = DateTime.UtcNow.AddDays(1);
            }

            items = items.Where(t => t.LastModifiedDate >= dateFrom.Value && t.LastModifiedDate <= dateTo.Value);

            switch (sortBy)
            {
                case "account":
                    items = sortDir == "desc" ? items.OrderByDescending(t => t.Account.UserName).ThenByDescending(t => t.PurchaseJob.LastModifiedDate)
                        : items.OrderBy(t => t.Account.UserName).ThenBy(t => t.PurchaseJob.LastModifiedDate);
                    break;
                case "status":
                    items = sortDir == "desc" ? items.OrderByDescending(t => t.Status).ThenByDescending(t => t.PurchaseJob.LastModifiedDate)
                        : items.OrderBy(t => t.Status).ThenBy(t => t.PurchaseJob.LastModifiedDate);
                    break;
                case "site":
                    items = sortDir == "desc" ? items.OrderByDescending(t => t.PurchaseJob.SiteId).ThenByDescending(t => t.PurchaseJob.LastModifiedDate)
                        : items.OrderBy(t => t.PurchaseJob.SiteId).ThenBy(t => t.PurchaseJob.LastModifiedDate);
                    break;
                case "scheduleRuntime":
                    items = sortDir == "desc" ? items.OrderByDescending(t => t.ScheduleRuntime).ThenByDescending(t => t.LastModifiedDate) : items.OrderBy(t => t.ScheduleRuntime).ThenBy(t => t.LastModifiedDate);
                    break;
                case "lastModifiedDate":
                default:
                    items = sortDir == "desc" ? items.OrderByDescending(t => t.LastModifiedDate) : items.OrderBy(t => t.LastModifiedDate);
                    break;
            }

            var count = items.Count();

            items = items.Skip(pageIndex * pageSize).Take(pageSize);

            var data = await items.ToListAsync();

            return Ok(new PaginatedResponse<DTO.PurchaseJobOrder>(data.Select(t => mapper.Map<Domain.PurchaseJobOrder, DTO.PurchaseJobOrder>(t)), pageSize, pageIndex, count));
        }


        [HttpGet]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [ClaimsFilter]
        [Route("{id}")]
        public async Task<IActionResult> Get(Guid id)
        {
            var item = await databaseContext.PurchaseJobOrders
                .Include(t => t.Account)
                .Include(t => t.CreatedBy)
                .Include(t => t.Shipping)
                .Include(t => t.Payment)
                .Include(t => t.Affiliate)
                .Include(t => t.DiscountCodes)
                .Include(t => t.LineItems)
                .Include(t => t.Result)
                .ThenInclude(t => t.ScreenShots)
                .Include(t => t.PurchaseJob)
                .ThenInclude(t => t.Site)
                .FirstOrDefaultAsync(t => !t.Deleted && t.Id == id);

            if (item == null)
            {
                return NotFound("id");
            }

            var job = mapper.Map<Domain.PurchaseJobOrder, DTO.PurchaseJobOrder>(item);

            return Ok(job);
        }


        [HttpGet]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [ClaimsFilter]
        [Route("clone/{id}")]
        public async Task<IActionResult> Clone(Guid id)
        {
            var jobOrder = await databaseContext.PurchaseJobOrders
                .Include(t => t.Account)
                .Include(t => t.CreatedBy)
                .Include(t => t.Shipping)
                .Include(t => t.Payment)
                .Include(t => t.Affiliate)
                .Include(t => t.DiscountCodes)
                .Include(t => t.LineItems)
                .Include(t => t.PurchaseJob)
                .FirstOrDefaultAsync(t => t.Id == id);

            if (jobOrder == null)
            {
                return NotFound("id");
            }

            var job = mapper.Map<Domain.PurchaseJobOrder, DTO.PurchaseJobOrder>(jobOrder);
            return Ok(job);
        }

        [HttpPost]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "admin")]
        [ClaimsFilter]
        public async Task<IActionResult> PostAsync(string userId, [FromBody] CreatePurchaseJob body)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var job = new Domain.PurchaseJob(body.SiteId, body.IsTest, userId);

            foreach (var jobOrderDTO in body.PurchaseJobOrders)
            {
                for (var i = 0; i < jobOrderDTO.Copies; i++)
                {
                    var jobOrder = new Domain.PurchaseJobOrder(jobOrderDTO.ShippingId, jobOrderDTO.PaymentId, jobOrderDTO.PriceLimit, jobOrderDTO.AccountId, jobOrderDTO.AffiliateId, jobOrderDTO.AffiliateAccountId, jobOrderDTO.PurchaseDateTime, userId)
                    {
                        GuestCheckoutEmail = jobOrderDTO.GuestCheckoutEmail
                    };

                    foreach (var o in jobOrderDTO.LineItems)
                    {
                        jobOrder.AddLineItem(o.InputType, o.ProductName, o.Input, o.Options, o.Amount, o.Price, userId);
                    }

                    job.AddJobOrder(jobOrder);
                }
            }

            databaseContext.PurchaseJobs.Add(job);
            await databaseContext.SaveChangesAsync();
            var item = mapper.Map<Domain.PurchaseJob, DTO.PurchaseJob>(job);
            return Ok(item);
        }


        [HttpPost("{id}/process")]
        public async Task<IActionResult> PostAsync(Guid id)
        {
            var item = await databaseContext.PurchaseJobOrders
               .Include(t => t.Result)
               .Include(t => t.Account)
               .Where(t => !t.Deleted)
               .FirstOrDefaultAsync(x => x.Id == id);

            if (item == null)
            {
                return NotFound("id");
            }

            item.Queued();
            await databaseContext.SaveChangesAsync();

            QueueClient jobQueue;

            if (item.Account == null || string.IsNullOrEmpty(item.Account.QueueName))
            {
                jobQueue = new QueueClient(configuration["ServiceBusConnectionString"], configuration["ServiceBusQueueName"]);
            }
            else
            {
                jobQueue = new QueueClient(configuration["ServiceBusConnectionString"], item.Account.QueueName);
            }

            var message = new Message(Encoding.UTF8.GetBytes("1 " + id.ToString()));
            message.TimeToLive = new TimeSpan(4, 0, 0);
            await jobQueue.SendAsync(message);

            return Ok(item);
        }

        [HttpPost("cancel")]
        public async Task<ActionResult> CancelPurchaseJobOrdersAsync()
		{
            var purchaseJobs = await databaseContext.PurchaseJobOrders.Where(x => x.Status == PurchaseJobStatus.New || x.Status == PurchaseJobStatus.Queued).ToListAsync();

            foreach (var purchaseJob in purchaseJobs)
			{
                purchaseJob.Status = PurchaseJobStatus.Cancelled;
            }

            await databaseContext.SaveChangesAsync();

            return Ok();
		}
    }
}