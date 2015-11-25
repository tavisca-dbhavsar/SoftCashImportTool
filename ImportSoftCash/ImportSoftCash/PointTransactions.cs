
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ImportSoftCash
{

    public class PointTransaction
    {
        public long PointTransactionId { get; set; }
        public long CustomerID { get; set; }
        public int PointAccountID { get; set; }
        public decimal Amount { get; set; }
        public DateTime TransactionDate { get; set; }
    }

}
