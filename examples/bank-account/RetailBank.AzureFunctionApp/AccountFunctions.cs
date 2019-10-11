using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;

using EventSourcingOnAzureFunctions.Common.Binding;
using EventSourcingOnAzureFunctions.Common.EventSourcing;

using RetailBank.AzureFunctionApp.Account.Projections;
using System;
using static EventSourcingOnAzureFunctions.Common.EventSourcing.Implementation.EventStreamBase;
using EventSourcingOnAzureFunctions.Common.EventSourcing.Exceptions;

namespace RetailBank.AzureFunctionApp
{
    public  class AccountFunctions
    {

        /// <summary>
        /// Open a new bank account
        /// </summary>
        /// <param name="accountnumber">
        /// The account number to use for the account.  If this already exists this command will return an error.
        /// </param>
        /// <returns></returns>
        [FunctionName("OpenAccount")]
        public static async Task<HttpResponseMessage> OpenAccountRun(
                      [HttpTrigger(AuthorizationLevel.Function, "POST", Route = @"OpenAccount/{accountnumber}")]HttpRequestMessage req,
                      string accountnumber,
                      [EventStream("Bank", "Account", "{accountnumber}")]  EventStream bankAccountEvents)
        {

            // Set the start time for how long it took to process the message
            DateTime startTime = DateTime.UtcNow; 

            if (await bankAccountEvents.Exists())
            {
                return req.CreateResponse<FunctionResponse>(System.Net.HttpStatusCode.Forbidden,
                    FunctionResponse.CreateResponse(startTime, 
                    true,
                    $"Account {accountnumber} already exists"),
                    FunctionResponse.MEDIA_TYPE);
            }
            else
            {
                // Get request body
                AccountOpeningData data = await req.Content.ReadAsAsync<AccountOpeningData>();

                // Append a "created" event
                DateTime dateCreated = DateTime.UtcNow;
                Account.Events.Opened evtOpened = new Account.Events.Opened() { LoggedOpeningDate = dateCreated };
                if (!string.IsNullOrWhiteSpace(data.Commentary))
                {
                    evtOpened.Commentary = data.Commentary;
                }
                try
                {
                    await bankAccountEvents.AppendEvent(evtOpened,
                        streamConstraint: EventStreamExistenceConstraint.MustBeNew
                        );
                }
                catch (EventStreamWriteException exWrite)
                {
                    return req.CreateResponse<FunctionResponse>(System.Net.HttpStatusCode.Conflict,
                        FunctionResponse.CreateResponse(startTime,
                        true,
                        $"Account {accountnumber} had a conflict error on creation {exWrite.Message }"),
                        FunctionResponse.MEDIA_TYPE);
                }

                // If there is an initial deposit in the account opening data, append a "deposit" event
                if (data.OpeningBalance.HasValue)
                {
                    Account.Events.MoneyDeposited evtInitialDeposit = new Account.Events.MoneyDeposited()
                    {
                        AmountDeposited = data.OpeningBalance.Value,
                        LoggedDepositDate = dateCreated,
                        Commentary = "Opening deposit"
                    };
                    await bankAccountEvents.AppendEvent(evtInitialDeposit);
                }

                // If there is a beneficiary in the account opening data append a "beneficiary set" event
                if (!string.IsNullOrEmpty(data.ClientName))
                {
                    Account.Events.BeneficiarySet evtBeneficiary = new Account.Events.BeneficiarySet()
                    { BeneficiaryName = data.ClientName };
                    await bankAccountEvents.AppendEvent(evtBeneficiary);
                }

                return req.CreateResponse<FunctionResponse>(System.Net.HttpStatusCode.Created,
                    FunctionResponse.CreateResponse(startTime,
                    false,
                    $"Account { accountnumber} created"),
                    FunctionResponse.MEDIA_TYPE);

            }
        }

        /// <summary>
        /// Get the current balance of a bank account
        /// </summary>
        /// <param name="req"></param>
        /// <param name="accountnumber">
        /// The account number of the account for which we want the balance
        /// </param>
        /// <param name="prjBankAccountBalance">
        /// The projection instance that is run to get the current account balance
        /// </param>
        /// <returns></returns>
        [FunctionName("GetBalance")]
        public static async Task<HttpResponseMessage> GetBalanceRun(
          [HttpTrigger(AuthorizationLevel.Function, "GET", Route = @"GetBalance/{accountnumber}/{asOfDate?}" )]HttpRequestMessage req,
          string accountnumber,
          string asOfDate,
          [Projection("Bank", "Account", "{accountnumber}", nameof(Balance))] Projection prjBankAccountBalance)
        {

            // Set the start time for how long it took to process the message
            DateTime startTime = DateTime.UtcNow;

            string result = $"No balance found for account {accountnumber}";

            if (null != prjBankAccountBalance)
            {
                if (await prjBankAccountBalance.Exists())
                {
                    // Get request body
                    Nullable<DateTime> asOfDateValue = null;
                    if (! string.IsNullOrEmpty(asOfDate) )
                    {
                        DateTime dtTest;
                        if( DateTime.TryParse(asOfDate, out dtTest ))
                        {
                            asOfDateValue = dtTest;
                        }
                    }
                    
                    Balance projectedBalance = await prjBankAccountBalance.Process<Balance>(asOfDateValue);
                    if (null != projectedBalance)
                    {
                        result = $"Balance for account {accountnumber} is ${projectedBalance.CurrentBalance} (As at record {projectedBalance.CurrentSequenceNumber}) ";
                        return req.CreateResponse<ProjectionFunctionResponse>(System.Net.HttpStatusCode.OK,
                                ProjectionFunctionResponse.CreateResponse(startTime,
                                false,
                                result,
                                projectedBalance.CurrentSequenceNumber ),
                                FunctionResponse.MEDIA_TYPE);
                    }
                }
                else
                {
                    result = $"Account {accountnumber} is not yet created - cannot retrieve a balance for it";
                    return req.CreateResponse<ProjectionFunctionResponse>(System.Net.HttpStatusCode.NotFound ,
                        ProjectionFunctionResponse.CreateResponse(startTime,
                        true,
                        result,
                        0),
                        FunctionResponse.MEDIA_TYPE);
                }
            }

            // If we got here no balance was found
            return req.CreateResponse<ProjectionFunctionResponse>(System.Net.HttpStatusCode.NotFound,
                ProjectionFunctionResponse.CreateResponse(startTime,
                true,
                result,
                0),
                FunctionResponse.MEDIA_TYPE);

        }


        [FunctionName("DepositMoney")]
        public static async Task<HttpResponseMessage> DepositMoneyRun(
              [HttpTrigger(AuthorizationLevel.Function, "POST", Route = @"DepositMoney/{accountnumber}")]HttpRequestMessage req,
              string accountnumber,
              [EventStream("Bank", "Account", "{accountnumber}")]  EventStream bankAccountEvents)
        {

            // Set the start time for how long it took to process the message
            DateTime startTime = DateTime.UtcNow;

            if (!await bankAccountEvents.Exists())
            {
                return req.CreateResponse<FunctionResponse>(System.Net.HttpStatusCode.NotFound ,
                        FunctionResponse.CreateResponse(startTime,
                        true,
                        $"Account {accountnumber} does not exist"),
                        FunctionResponse.MEDIA_TYPE);
            }
            else
            {
                // get the request body...
                MoneyDepositData data = await req.Content.ReadAsAsync<MoneyDepositData>();

                // create a deposited event
                DateTime dateDeposited = DateTime.UtcNow;
                Account.Events.MoneyDeposited evDeposited = new Account.Events.MoneyDeposited()
                {
                    LoggedDepositDate = dateDeposited,
                    AmountDeposited = data.DepositAmount,
                    Commentary = data.Commentary,
                    Source = data.Source
                };

                await bankAccountEvents.AppendEvent(evDeposited);

                return req.CreateResponse<FunctionResponse>(System.Net.HttpStatusCode.OK ,
                        FunctionResponse.CreateResponse(startTime,
                        false,
                        $"{data.DepositAmount} deposited to account {accountnumber} "),
                        FunctionResponse.MEDIA_TYPE);

            }
        }


        // WithdrawMoney
        [FunctionName("WithdrawMoney")]
        public static async Task<HttpResponseMessage> WithdrawMoneyRun(
              [HttpTrigger(AuthorizationLevel.Function, "POST", Route = @"WithdrawMoney/{accountnumber}")]HttpRequestMessage req,
              string accountnumber,
              [EventStream("Bank", "Account", "{accountnumber}")]  EventStream bankAccountEvents,
              [Projection("Bank", "Account", "{accountnumber}", nameof(Balance))] Projection prjBankAccountBalance,
              [Projection("Bank", "Account", "{accountnumber}", nameof(OverdraftLimit))] Projection prjBankAccountOverdraft)
        {

            // Set the start time for how long it took to process the message
            DateTime startTime = DateTime.UtcNow;

            if (!await bankAccountEvents.Exists())
            {
                return req.CreateResponse<ProjectionFunctionResponse>(System.Net.HttpStatusCode.NotFound,
                        ProjectionFunctionResponse.CreateResponse(startTime,
                        true,
                        $"Account {accountnumber} does not exist",
                        0),
                        FunctionResponse.MEDIA_TYPE);
            }
            else
            {
                // get the request body...
                MoneyWithdrawnData  data = await req.Content.ReadAsAsync<MoneyWithdrawnData>();

                // get the current account balance
                Balance projectedBalance = await prjBankAccountBalance.Process<Balance>();
                if (null != projectedBalance)
                {
                    OverdraftLimit projectedOverdraft = await prjBankAccountOverdraft.Process<OverdraftLimit>();

                    decimal overdraftSet = 0.00M;
                    if (null != projectedOverdraft )
                    {
                        if (projectedOverdraft.CurrentSequenceNumber != projectedBalance.CurrentSequenceNumber   )
                        {
                            // The two projectsions are out of synch.  In a real business case we would retry them 
                            // n times to try and get a match but here we will just throw a consistency error
                            return req.CreateResponse<ProjectionFunctionResponse>(System.Net.HttpStatusCode.Forbidden,
                                    ProjectionFunctionResponse.CreateResponse(startTime,
                                    true,
                                    $"Unable to get a matching state for the current balance and overdraft for account {accountnumber}",
                                    0),
                                    FunctionResponse.MEDIA_TYPE);
                        }
                        else
                        {
                            overdraftSet = projectedOverdraft.CurrentOverdraftLimit; 
                        }
                    }

                    if ((projectedBalance.CurrentBalance + overdraftSet) >= data.AmountWithdrawn)
                    {
                        // attempt the withdrawal
                        DateTime dateWithdrawn = DateTime.UtcNow;
                        Account.Events.MoneyWithdrawn evWithdrawn = new Account.Events.MoneyWithdrawn()
                        {
                            LoggedWithdrawalDate = dateWithdrawn,
                            AmountWithdrawn = data.AmountWithdrawn ,
                            Commentary = data.Commentary 
                        };
                        try
                        {
                            await bankAccountEvents.AppendEvent(evWithdrawn, projectedBalance.CurrentSequenceNumber);
                        }
                        catch (EventSourcingOnAzureFunctions.Common.EventSourcing.Exceptions.EventStreamWriteException exWrite  )
                        {
                            return req.CreateResponse<ProjectionFunctionResponse>(System.Net.HttpStatusCode.Forbidden,
                                    ProjectionFunctionResponse.CreateResponse(startTime,
                                    true,
                                    $"Failed to write withdrawal event {exWrite.Message}",
                                    0),
                                    FunctionResponse.MEDIA_TYPE);

                        }

                        return req.CreateResponse<ProjectionFunctionResponse>(System.Net.HttpStatusCode.OK,
                            ProjectionFunctionResponse.CreateResponse(startTime,
                            false,
                            $"{data.AmountWithdrawn } withdrawn from account {accountnumber} (New balance: {projectedBalance.CurrentBalance - data.AmountWithdrawn}, overdraft: {overdraftSet} )",
                            projectedBalance.CurrentSequenceNumber ),
                            FunctionResponse.MEDIA_TYPE);
                    }
                    else
                    {

                        return req.CreateResponse<ProjectionFunctionResponse>(System.Net.HttpStatusCode.Forbidden,
                                ProjectionFunctionResponse.CreateResponse(startTime,
                                true,
                                $"Account {accountnumber} does not have sufficent funds for the withdrawal of {data.AmountWithdrawn} (Current balance: {projectedBalance.CurrentBalance}, overdraft: {overdraftSet} )",
                                projectedBalance.CurrentSequenceNumber ),
                                FunctionResponse.MEDIA_TYPE);

                            
                    }
                }
                else
                {
                    return req.CreateResponse<ProjectionFunctionResponse>(System.Net.HttpStatusCode.Forbidden,
                                ProjectionFunctionResponse.CreateResponse(startTime,
                                true,   
                                $"Unable to get current balance for account {accountnumber}", 
                                projectedBalance.CurrentSequenceNumber),
                                FunctionResponse.MEDIA_TYPE);
                }
            }
        }


        /// <summary>
        /// Set or change who is the beneficial owner of this account
        /// </summary>
        /// <returns></returns>
        [FunctionName("SetBeneficialOwner") ]
        public static async Task<HttpResponseMessage> SetBeneficialOwnerRun(
              [HttpTrigger(AuthorizationLevel.Function, "POST", Route = "SetBeneficialOwner/{accountnumber}/{ownername}")]HttpRequestMessage req,
              string accountnumber,
              string ownername,
              [EventStream("Bank", "Account", "{accountnumber}")]  EventStream bankAccountEvents)
        {

            // Set the start time for how long it took to process the message
            DateTime startTime = DateTime.UtcNow;

            if (await bankAccountEvents.Exists())
            {
                if (!string.IsNullOrEmpty(ownername))
                {
                    Account.Events.BeneficiarySet evtBeneficiary = new Account.Events.BeneficiarySet()
                    { BeneficiaryName = ownername };
                    await bankAccountEvents.AppendEvent(evtBeneficiary);
                }

                return req.CreateResponse<FunctionResponse>(System.Net.HttpStatusCode.OK,
                        FunctionResponse.CreateResponse(startTime,
                        false,
                        $"Beneficial owner of account {accountnumber} set"),
                        FunctionResponse.MEDIA_TYPE);

            }
            else
            {
                return req.CreateResponse<FunctionResponse>(System.Net.HttpStatusCode.OK,
                    FunctionResponse.CreateResponse(startTime,
                    true,
                    $"Account {accountnumber} does not exist"),
                    FunctionResponse.MEDIA_TYPE);
            }
        }


        /// <summary>
        /// Set a new overdraft limit for the account
        /// </summary>
        [FunctionName("SetOverdraftLimit")]
        public static async Task<HttpResponseMessage> SetOverdraftLimitRun(
          [HttpTrigger(AuthorizationLevel.Function, "POST", Route = @"SetOverdraftLimit/{accountnumber}")]HttpRequestMessage req,
          string accountnumber,
          [EventStream("Bank", "Account", "{accountnumber}")]  EventStream bankAccountEvents,
          [Projection("Bank", "Account", "{accountnumber}", nameof(Balance))] Projection prjBankAccountBalance)
        {

            // Set the start time for how long it took to process the message
            DateTime startTime = DateTime.UtcNow;

            if (!await bankAccountEvents.Exists())
            {
                // You cannot set an overdraft if the account does not exist
                return req.CreateResponse<ProjectionFunctionResponse>(System.Net.HttpStatusCode.Forbidden ,
                    ProjectionFunctionResponse.CreateResponse(startTime,
                    true,
                    $"Account {accountnumber} does not exist",
                    0));
            }
            else
            {
                // get the request body...
                OverdraftSetData data = await req.Content.ReadAsAsync<OverdraftSetData>();

                // get the current account balance
                Balance projectedBalance = await prjBankAccountBalance.Process<Balance>();
                if (null != projectedBalance)
                {
                    if (projectedBalance.CurrentBalance >= (0 - data.NewOverdraftLimit) )
                    {
                        // attempt to set the new overdraft limit
                        Account.Events.OverdraftLimitSet evOverdraftSet = new Account.Events.OverdraftLimitSet()
                        {
                            OverdraftLimit = data.NewOverdraftLimit ,
                            Commentary = data.Commentary
                        };
                        try
                        {
                            await bankAccountEvents.AppendEvent(evOverdraftSet, projectedBalance.CurrentSequenceNumber);
                        }
                        catch (EventSourcingOnAzureFunctions.Common.EventSourcing.Exceptions.EventStreamWriteException exWrite)
                        {
                            return req.CreateResponse<ProjectionFunctionResponse>(System.Net.HttpStatusCode.Forbidden,
                                ProjectionFunctionResponse.CreateResponse(startTime,
                                true,
                                $"Failed to write overdraft limit event {exWrite.Message}",
                                projectedBalance.CurrentSequenceNumber  ),
                                FunctionResponse.MEDIA_TYPE);

                        }

                        return req.CreateResponse<ProjectionFunctionResponse>(System.Net.HttpStatusCode.OK ,
                            ProjectionFunctionResponse.CreateResponse(startTime,
                            false,
                            $"{data.NewOverdraftLimit } set as the new overdraft limit for account {accountnumber}",
                            projectedBalance.CurrentSequenceNumber),
                            FunctionResponse.MEDIA_TYPE);

                    }
                    else
                    {
                        return req.CreateResponse<ProjectionFunctionResponse>(System.Net.HttpStatusCode.Forbidden ,
                            ProjectionFunctionResponse.CreateResponse(startTime,
                            true,
                            $"Account {accountnumber} has an outstanding balance greater than the new limit {data.NewOverdraftLimit} (Current balance: {projectedBalance.CurrentBalance} )",
                            projectedBalance.CurrentSequenceNumber),
                            FunctionResponse.MEDIA_TYPE
                            );

                    }
                }
                else
                {
                    return req.CreateResponse<ProjectionFunctionResponse>(System.Net.HttpStatusCode.Forbidden,
                            ProjectionFunctionResponse.CreateResponse(startTime,
                            true,
                            $"Unable to get current balance for account {accountnumber}",
                            0),
                            FunctionResponse.MEDIA_TYPE 
                            );
                }
            }
        }
    }
}
