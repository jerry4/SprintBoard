using System.Runtime.Caching;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Web;
using System.Web.Http;

namespace SprintBoard.Controllers
{
  public class SprintController : ApiController
  {
    private static readonly ObjectCache _cache = MemoryCache.Default;
    public T GetCache<T>(string key) where T : class
    {
      return _cache[key] as T;
    }

    private HttpClient Client
    {
      get
      {
        var client = new HttpClient();
        client.BaseAddress = new Uri("https://siftit.atlassian.net/");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Request.Headers.Authorization.Parameter);
        return client;
      }
    }

    // yeah I don't get this. There are multiple names depending on what you are doing...
    private readonly string _projectname = "Evolution";
    private readonly string _jiraProjectName = "Iterations";

    public string GetUserData(String username)
    {
      if (_cache.Contains("users-" + username))
      {
        return GetCache<string>("users-" + username);
      }

      // there was a chache miss...
      var userUri = "rest/api/2/user?username=" + username;
      var userResponse = Client.GetAsync(userUri).Result;

      string userData = string.Empty;

      if (userResponse.IsSuccessStatusCode)
      {
        userData = userResponse.Content.ReadAsStringAsync().Result;
        _cache.Set("users-" + username, userData, DateTimeOffset.Now.AddDays(7));
      }
      else
      {
        throw new HttpResponseException(userResponse);
      }

      return userData;
    }

    public HttpResponseMessage Get(string project = "Evolution", string sprint = null)
    {
      if (string.IsNullOrEmpty(project)) { project = _projectname; }

      var jql = String.Format("Project = {0} AND Sprint {1} ORDER BY rank",
          project,
          sprint == null ? "in openSprints()" : String.Format("=\"Sprint {0}\"", sprint));

      var uri = String.Format("rest/api/2/search?maxResults={0}&jql={1}&fields={2}",
          2000,
          HttpContext.Current.Server.UrlEncode(jql),
          "summary,issuetype,created,updated,priority,description,status,parent,assignee,customfield_10004,customfield_10008,labels,subtasks");

      HttpResponseMessage response = Client.GetAsync(uri).Result;
      string issues = string.Empty;
      if (response.IsSuccessStatusCode)
      {
        // Parse the response body. Blocking!
        issues = response.Content.ReadAsStringAsync().Result;
        issues = issues.Replace("customfield_10004", "storyPoints");
        issues = issues.Replace("customfield_10008", "epicLink");
      }
      else
      {
        throw new HttpResponseException(response);
      }

      return Request.CreateResponse(HttpStatusCode.Accepted, JsonConvert.DeserializeObject<object>(issues));
    }

    [ActionName("getepics")]
    public async Task<HttpResponseMessage> GetEpics([FromUri]IEnumerable<string> epics)
    {
      epics = epics.Distinct();
      var epicDict = new Dictionary<string, string>();
      if (_cache.Contains("epics"))
      {
        epicDict = GetCache<Dictionary<string, string>>("epics");
      }

      if (epics.All(key => epicDict.ContainsKey(key)))
      {
        return Request.CreateResponse(HttpStatusCode.OK, epicDict);
      }

      // there was a chache miss...
      var client = Client;
      var taskList =
          epics.Select(
              e => client.GetAsync("rest/api/2/issue/" + e + "?fields=summary"));


      var results = (await Task.WhenAll(taskList))
          .Select(r => r.Content.ReadAsAsync<JiraIssueResponse>().Result)
              .ToDictionary(r => r.key, r => r.fields.summary);

      // merge for cache probably chance of concurrency issue...
      var mergedDict = (new List<Dictionary<string, string>>() { epicDict, results })
          .SelectMany(dict => dict)
          .ToLookup(pair => pair.Key, pair => pair.Value)
          .ToDictionary(group => group.Key, group => group.Last());

      _cache.Set("epics", mergedDict, DateTimeOffset.Now.AddHours(4));

      return Request.CreateResponse(HttpStatusCode.OK, mergedDict);
    }

    public HttpResponseMessage GetSprints()
    {
      string projects = string.Empty;
      if (_cache.Contains("projects"))
      {
        projects = GetCache<string>("projects");
      }
      else
      {
        projects = Client.GetStringAsync("rest/greenhopper/1.0/rapidview").Result;
        _cache.Set("projects", projects, DateTimeOffset.Now.AddDays(15));
      }
      var jiraProjects = JsonConvert.DeserializeObject<JiraProjects>(projects);
      var proj = jiraProjects.views.First(v => v.name.Contains(_jiraProjectName));

      string sprints = string.Empty;
      if (_cache.Contains("sprints"))
      {
        sprints = GetCache<string>("sprints");
      }
      else
      {
        sprints = Client.GetStringAsync("rest/greenhopper/1.0/sprintquery/" + proj.id).Result;
        _cache.Set("sprints", sprints, DateTimeOffset.Now.AddHours(4));
      }
      return Request.CreateResponse(HttpStatusCode.OK, sprints);
    }

    [HttpPut]
    public HttpResponseMessage Assign([FromBody]AssignToMeParams assignParams)
    {
      var uri = "rest/api/2/issue/" + assignParams.issueId;


      var issue = new
      {
        fields = new
        {
          assignee = new
          {
            name = assignParams.userName
          }
        }
      };
      var result = Client.PutAsJsonAsync(uri, issue).Result;

      return result;
    }

    public class UpdateStateParams
    {
      public String issueId { get; set; }
      public String userName { get; set; }
      public Boolean updateAssignment { get; set; }
      public Int16 state { get; set; }
    }

    [HttpPut]
    public HttpResponseMessage UpdateState([FromBody]UpdateStateParams updateParams)
    {
      HttpResponseMessage assignResponse = null;
      if (updateParams.updateAssignment)
      {
        assignResponse = Assign(new AssignToMeParams
        {
          issueId = updateParams.issueId,
          userName = updateParams.userName
        });
      }

      if (assignResponse == null || assignResponse.StatusCode == HttpStatusCode.NoContent)
      {
        var uri = "rest/api/2/issue/" + updateParams.issueId + "/transitions";

        var postParams = new
        {
          transition = new
          {
            id = updateParams.state
          }
        };
        var result = Client.PostAsJsonAsync(uri, postParams).Result;

        return result;
      }

      return assignResponse;
    }

    public class AddSubTasks
    {
      public string Parent { get; set; }
      public List<string> Tasks { get; set; }
    }

    [HttpPut]
    public HttpResponseMessage PutSubTasks(AddSubTasks tasks)
    {
      if (!tasks.Tasks.Any())
      {
        return new HttpResponseMessage(HttpStatusCode.BadRequest);
      }

      var uri = "rest/api/2/issue/";

      var resultList = new List<Task<HttpResponseMessage>>();
      var taskIndex = 1;
      var client = Client;
      foreach (var task in tasks.Tasks.Where(x => !String.IsNullOrWhiteSpace(x)))
      {
        var issue = new
        {
          fields = new
          {
            project = new
            {
              key = tasks.Parent.Split('-')[0]//MRB
            },
            parent = new
            {
              key = tasks.Parent
            },
            summary = String.Format("{0} - {1}", taskIndex, task),
            description = "",
            issuetype = new
            {
              name = "Technical Task"
            }
          }
        };
        resultList.Add(client.PostAsJsonAsync(uri, issue));
        taskIndex++;
      }
      Task.WaitAll(resultList.ToArray());
      // bad but oh well...
      return new HttpResponseMessage(HttpStatusCode.Created);
    }
  }

  public class AssignToMeParams
  {
    public String issueId { get; set; }
    public String userName { get; set; }
  }

  public class Fields
  {
    public string summary { get; set; }
  }

  public class JiraIssueResponse
  {
    //public string expand { get; set; }
    //public string id { get; set; }
    //public string self { get; set; }
    public string key { get; set; }
    public Fields fields { get; set; }
  }

  public class View
  {
    public int id { get; set; }
    public string name { get; set; }
    public bool canEdit { get; set; }
    public bool sprintSupportEnabled { get; set; }
  }

  public class JiraProjects
  {
    public List<View> views { get; set; }
  }
}
