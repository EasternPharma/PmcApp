using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PMC_APP.DTOs;

public class ArticleFilterDTO
{
    public int PmcID { get; set; }
    public List<string> Keywords { get; set; }
    public List<string> Categories { get; set; }
    public string Title { get; set; }
    public string Link { get; set; }
}

public class SearchArticleDTO : ArticleFilterDTO
{
    public bool IsSearch { get; set; }
}