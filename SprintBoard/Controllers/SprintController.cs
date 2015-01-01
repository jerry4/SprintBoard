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
        private readonly string _baseUrl = "https://siftit.atlassian.net/rest/api/2/";
        private readonly string _jiraProject = "Ordering";

        public string GetUserData(String username)
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", Request.Headers.Authorization.Parameter);

            var userUri = _baseUrl + "user?username=" + username;
            var userResponse = client.GetAsync(userUri).Result;
            
            string userData = string.Empty;

            if (userResponse.IsSuccessStatusCode)
            {
                userData = userResponse.Content.ReadAsStringAsync().Result;
            }
            else
            {
                throw new HttpResponseException(userResponse);
            }

            return userData;
        }

        public string Get(string project = "", string sprint = null)
        {
            if (string.IsNullOrEmpty(project)) { project = _jiraProject; }

            var client = new HttpClient();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", Request.Headers.Authorization.Parameter);

            var jql = String.Format("Project = {0} AND Sprint {1} ORDER BY rank",
                project,
                sprint == null ? "in openSprints()" : String.Format("=\"Sprint {0}\"", sprint));

            var uri = String.Format(_baseUrl + "search?maxResults={0}&jql={1}",
                2000,
                HttpContext.Current.Server.UrlEncode(jql));
            //var uri = String.Format(_baseUrl + "search?maxResults={0}&jql={1}&fields={2}",
            //    2000,
            //    HttpContext.Current.Server.UrlEncode(jql),
            //    "summary,issuetype,created,updated,priority,description,status,parent,assignee,customfield_10004,labels,subtasks");
            
            HttpResponseMessage response = client.GetAsync(uri).Result;
            string issues = string.Empty;
            if (response.IsSuccessStatusCode)
            {
                // Parse the response body. Blocking!
                issues = response.Content.ReadAsStringAsync().Result;
                issues = issues.Replace("customfield_10004", "storyPoints");
            }
            else
            {
                throw new HttpResponseException(response);
            }

            return issues;
        }
        
        public class AssignToMeParams
        {
            public String issueId { get; set; }
            public String userName { get; set; }
        }

        [HttpPut]
        public HttpResponseMessage Assign([FromBody]AssignToMeParams assignParams)
        {
            var uri = _baseUrl + "issue/" + assignParams.issueId;

            var client = new HttpClient();

            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", Request.Headers.Authorization.Parameter);

            var issue = new {
                fields = new {
                    assignee = new {
                        name = assignParams.userName
                    }
                }
            };

            var result = client.PutAsJsonAsync(uri, issue).Result;

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
                var uri = _baseUrl + "issue/" + updateParams.issueId + "/transitions";

                var client = new HttpClient();

                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Basic", Request.Headers.Authorization.Parameter);

                var postParams = new {
                    transition = new {
                        id = updateParams.state
                    }                
                };
                var result = client.PostAsJsonAsync(uri, postParams).Result;

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

            var uri = _baseUrl + "issue/";

            var client = new HttpClient();

            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", Request.Headers.Authorization.Parameter);

            var resultList = new List<Task<HttpResponseMessage>>();
            var taskIndex = 1;
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
}
