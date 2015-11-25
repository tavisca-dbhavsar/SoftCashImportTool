using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.Services.Client;
using System.IO;
using System.Linq;
using System.Runtime.Remoting.Contexts;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vexiere.TransactionService;
using ImportSoftCash.ExigoModel;


namespace ImportSoftCash
{
    class Program
    {
        private static List<long> TransactionId=new List<long>();
        private static List<long> FailedTransactionId=new List<long>();
       
       private static TransactionServiceClient _transactionServiceClient = null;
       private static MemberServiceClient _memberServiceClient = null;
        private static object _lock = new object();
        public Program()
        {
            lock (_lock)
            {
                if (_transactionServiceClient == null)
                    _transactionServiceClient = new TransactionServiceClient();
                if (_memberServiceClient == null)
                    _memberServiceClient = new MemberServiceClient();
            }

        }

        static void Main(string[] args)
        {
           
            lock (_lock)
            {
                if (_transactionServiceClient == null)
                    _transactionServiceClient = new TransactionServiceClient();
                if (_memberServiceClient == null)
                    _memberServiceClient = new MemberServiceClient();
            }

            var allTransaction = GetTransaction();
            var filteredTransaction =ExcludeExistingTransaction(allTransaction);
            ProcessSoftCash(filteredTransaction);
            Log();
        }

        private static List<PointTransaction> ExcludeExistingTransaction(List<PointTransaction> allTransaction)
        {
            var existingtranscationIds = GetExecutedIds();
            foreach (var existingtranscationId in existingtranscationIds)
            {
                allTransaction.RemoveAll(x => x.PointTransactionId == existingtranscationId);
            }
            return allTransaction;
        }

        private static void Log()
        {
            var path = ConfigurationManager.AppSettings["Logs"];
            var file = new StreamWriter(path);
            file.WriteLine("Successful transactions:");
            file.WriteLine(String.Join(",", TransactionId.ToArray()));

            file.WriteLine("Failed transactions:");
            file.WriteLine(String.Join(",", FailedTransactionId.ToArray()));
           
            file.Close();


            
        }

        private static void ProcessSoftCash(List<PointTransaction> pointTransaction)
        {

            foreach (var pt in pointTransaction)
            {
                Thread.Sleep(100);
                try
                {
                    ImportMemberinVexiere(pt);

                    if (pt.Amount < 0)
                    {
                        var debittransaction = new SoftCashDebitTransaction()
                            {
                                Amount = new SoftCashTransactionAmount() { Value = pt.Amount * (-1) },
                                CategoryType = GetVexiereSoftCashCategory(pt.PointAccountID),
                                Timestamp = DateTime.UtcNow,
                                Comment = GetComment(pt.PointAccountID, pt.Amount),
                                Reason = Constants.Reason
                            };

                        var additionalData = new List<KeyValuePair>()
                            {
                                new KeyValuePair() {Key = Constants.SkipMinimumBalanceCheck, Value = "true"},
                                new KeyValuePair()
                                    {Key = Constants.MidasTransactionId, Value = pt.PointTransactionId.ToString()}
                            };

                        _transactionServiceClient.ProcessTransaction(new ApplicationContext(),
                                                  new NameBasedMemberIdentifier() { MemberName = pt.CustomerID.ToString() }, debittransaction,
                                                  additionalData);
                    }
                    else
                    {
                        var credittransaction = new SoftCashCreditTransaction()
                            {
                                Amount = new SoftCashTransactionAmount() { Value = pt.Amount },
                                CategoryType = GetVexiereSoftCashCategory(pt.PointAccountID),
                                Timestamp = DateTime.UtcNow,
                                Comment = GetComment(pt.PointAccountID, pt.Amount),
                                Reason = Constants.Reason
                            };

                        var additionalData = new List<KeyValuePair>()
                            {
                                new KeyValuePair()
                                    {Key = Constants.MidasTransactionId, Value = pt.PointTransactionId.ToString()}
                            };
                        _transactionServiceClient.ProcessTransaction(new ApplicationContext(),
                                                  new NameBasedMemberIdentifier() { MemberName = pt.CustomerID.ToString() }, credittransaction,
                                                  additionalData);
                    }

                    TransactionId.Add(pt.PointTransactionId);
                }
                catch (Exception ex)
                {
                    FailedTransactionId.Add(pt.PointTransactionId);
                }
            }

        }

        private static void ImportMemberinVexiere(PointTransaction transaction)
        {

            Member member = _memberServiceClient.GetMember(new ApplicationContext(), new NameBasedMemberIdentifier { MemberName = transaction.CustomerID.ToString() }, null);

            if (member == null)
            {

                var context = Context.Current.ExigoContext;
                var customer = context.Customers.Where(x => x.CustomerID == transaction.CustomerID).FirstOrDefault();
                if (customer != null)
                {
                    member = new Member();
                    member.CreationDate = DateTime.UtcNow;
                    member.MemberName = customer.CustomerID.ToString();
                    member.Email = customer.Email;
                    member.BirthDate = customer.BirthDate ?? DateTime.Now;
                    member.Name = new Name()
                    {
                        FirstName = customer.FirstName,
                        LastName = customer.LastName
                    };
                    member.SoftCashSetting = new SoftCashSetting()
                    {
                        DollarEquivalent = 1,
                        MinimumBalance = 200,
                        ExpiryDays = 365
                    };

                    _memberServiceClient.CreateMember(new ApplicationContext(), member, null);
                }
            }
        }

        private static string GetComment(int pointAccountId, decimal amount)
        {
            switch (pointAccountId)
            {
                case 4: return "DreamTrips Points from Initial Payment";
                case 5: return "DreamTrips Points Earned from Referrals";
                case 6: return "DreamTrips Points from Recurring Payment";

                default:
                    return amount > 0 ? Constants.Comment_Credit : Constants.Comment_Debit;
            }
        }

        private static string GetVexiereSoftCashCategory(int pointAccountId)
        {
            switch (pointAccountId)
            {
                case 1: return "TravelPoints";
                case 2: return "TrainingPoints";
                case 3: return "GiftCards";
                case 4:
                case 5: return "DreamTripPoints";
                case 6: return "DreamTripPoints_Accrued";
            }
            return string.Empty;
        }

        private static List<PointTransaction> GetTransaction()
        {
            List<PointTransaction> list=new List<PointTransaction>();
            
            //---Customer ID,Transaction ID,Amount,Transaction Date,Point Account----//
          
            var allSoftCash = new StreamReader(ConfigurationManager.AppSettings["AllSoftCash"]);
       
            while(!allSoftCash.EndOfStream)
            {
                string line = allSoftCash.ReadLine();
                var data = line.Split(',');
                var pt = new PointTransaction();
                pt.CustomerID = Convert.ToInt32(data[0]);
                pt.PointTransactionId = Convert.ToInt32(data[1]);
                pt.Amount = Convert.ToDecimal(data[2]);
                pt.TransactionDate = Convert.ToDateTime(data[3]);
                pt.PointAccountID = Convert.ToInt32(data[4]);

                //if (existingIds.Contains(pt.PointTransactionId.ToString()) == false)
                list.Add(pt);

            }
          
            return list;
        }

        private static List<string> GetRemaingIds(List<string> allIds, List<string> processedIds)
        {
             var remainingList = new List<string>();
             foreach (var pid in allIds)
            {
                if (!processedIds.Contains(pid))
                {
                    remainingList.Add(pid);
                }
               
            }
            return remainingList;
        }

        private static List<string> GetAllSoftCashIds()
        {
            var data = new List<string>();
            var inputFile = new StreamReader(ConfigurationManager.AppSettings["AllSoftCashIds"]);
            int count = 0;
            int.TryParse(ConfigurationManager.AppSettings["SoftCashIdsCount"], out count);

            Console.SetIn(inputFile);

            for (int i = 0; i < count; i++) // 41581
            {
                string transaction = Console.ReadLine();
                if (transaction != null)
                {
                    List<string> parts = transaction.Split(',').ToList();
                    data.Add(parts[0]);
                }
            }
            inputFile.Close();
            return data;
        }

        private static List<Int32> GetExecutedIds()
        {
            var executedIds = new List<Int32>();
            var allSoftCash = new StreamReader(ConfigurationManager.AppSettings["ExecutedSoftCashIds"]);

            while (!allSoftCash.EndOfStream)
            {
                string line = allSoftCash.ReadLine();
                var data = line.Split(',');
                var pt = new PointTransaction();
               executedIds.Add(Convert.ToInt32(data[0]));
            }

            return executedIds;
        }

        private static List<string> GetAllExecutedSoftCash()
        {
          var data = new List<string>();
            var ExecutedSoftCashIds = new StreamReader(ConfigurationManager.AppSettings["ExecutedSoftCashIds"]);
            int count = 0;
            int.TryParse(ConfigurationManager.AppSettings["ExecutedSoftCashCount"], out count);

            Console.SetIn(ExecutedSoftCashIds);

            for (int i = 0; i < count; i++) // 41581
            {
                string transaction = Console.ReadLine();
                if (transaction != null)
                {
                  List<string> parts = transaction.Split(',').ToList();
                    data.Add(parts[0]);
                }
            }
            ExecutedSoftCashIds.Close();
            return data;
        }
    }
    public static class Constants
    {
        public const string TimeFormat = "yyyy-MM-ddTHH:mm:ss.fff";
        public const string Reason = "Points imported from Midas";
        public const string Comment_Debit = "Points debited from Midas";
        public const string Comment_Credit = "Points credited from Midas";
        public const string MidasTransactionId = "MidasTransactionId";
        public const string SkipMinimumBalanceCheck = "SkipMinimumBalanceCheck";
    }

    public class Context
    {
        public static Context Current
        {
            get
            {
                var context = new ExigoContext(new Uri(Configuration.Exigo.Url));

                var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes(Configuration.Exigo.Username + ":" + Configuration.Exigo.Password));

                context.SendingRequest +=
                        (object s, SendingRequestEventArgs e) =>
                                e.RequestHeaders.Add("Authorization", "Basic " + credentials);

                context.IgnoreMissingProperties = true;
                return new Context { ExigoContext = context };
            }
        }

        public ExigoContext ExigoContext { get; set; }
    }

    public static class Configuration
    {
        public static class Exigo
        {
            public static string Url
            {
                get
                {
                    return ConfigurationManager.AppSettings["exigo.url"];
                }
            }

            public static string Username
            {
                get
                {
                    return ConfigurationManager.AppSettings["exigo.username"];
                }
            }

            public static string Password
            {
                get
                {
                    return ConfigurationManager.AppSettings["exigo.password"];
                }
            }
        }

        public static string ExigoExtensionReadConnectionString { get { return ConfigurationManager.ConnectionStrings["exigoExtension.read"].ConnectionString; } }

        public static string ExigoExtensionWriteConnectionString { get { return ConfigurationManager.ConnectionStrings["exigoExtension.write"].ConnectionString; } }

        public static decimal HeartRateInSeconds { get { return string.IsNullOrEmpty(ConfigurationManager.AppSettings["tcsHeartRateInSeconds"]) ? (24 * 60 * 60) : decimal.Parse(ConfigurationManager.AppSettings["tcsheartRateInSeconds"]); } }

        public static decimal HeartRateInSecondsOfSelectPoints { get { return string.IsNullOrEmpty(ConfigurationManager.AppSettings["stcsHeartRateInSeconds"]) ? (60 * 60) : decimal.Parse(ConfigurationManager.AppSettings["stcsHeartRateInSeconds"]); } }


    }
}
