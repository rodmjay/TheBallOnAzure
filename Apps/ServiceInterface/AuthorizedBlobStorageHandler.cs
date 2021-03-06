﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Data;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Web;
using System.Web.Helpers;
using System.Web.Security;
using AaltoGlobalImpact.OIP;
using AzureSupport;
using DotNetOpenAuth.OpenId.RelyingParty;
using Microsoft.WindowsAzure;
using Microsoft.WindowsAzure.StorageClient;
using TheBall;
using TheBall.CORE;

namespace WebInterface
{
    public class AuthorizedBlobStorageHandler : IHttpHandler
    {
        /// <summary>
        /// You will need to configure this handler in the web.config file of your 
        /// web and register it with IIS before being able to use it. For more information
        /// see the following link: http://go.microsoft.com/?linkid=8101007
        /// </summary>
        #region IHttpHandler Members

        public bool IsReusable
        {
            // Return false in case your Managed Handler cannot be reused for another request.
            // Usually this would be false in case you have some state information preserved per request.
            get { return true; }
        }

        private const string AuthPersonalPrefix = "/auth/account/";
        private const string AuthGroupPrefix = "/auth/grp/";
        private const string AuthAccountPrefix = "/auth/acc/";
        private const string AuthPrefix = "/auth/";
        private const string AboutPrefix = "/about/";
        private int AuthGroupPrefixLen;
        private int AuthPersonalPrefixLen;
        private int AuthAccountPrefixLen;
        private int AuthProcPrefixLen;
        private int AuthPrefixLen;
        private int GuidIDLen;


        public AuthorizedBlobStorageHandler()
        {
            AuthGroupPrefixLen = AuthGroupPrefix.Length;
            AuthPersonalPrefixLen = AuthPersonalPrefix.Length;
            AuthAccountPrefixLen = AuthAccountPrefix.Length;
            AuthPrefixLen = AuthPrefix.Length;
            GuidIDLen = Guid.Empty.ToString().Length;
        }

        public void ProcessRequest(HttpContext context)
        {
            string user = context.User.Identity.Name;
            bool isAuthenticated = String.IsNullOrEmpty(user) == false;
            var request = context.Request;
            var response = context.Response;
            WebSupport.InitializeContextStorage(context.Request);
            if (request.Path.StartsWith(AboutPrefix))
            {
                if(request.Path.EndsWith("/oip-layout-register.phtml"))
                    ProcessDynamicRegisterRequest(request, response);
                else
                    HandleAboutGetRequest(context, request.Path);
                return;
            }

            if (isAuthenticated == false)
            {
                return;
            }
            try
            {
                if (request.Path.StartsWith(AuthPersonalPrefix))
                {
                    HandlePersonalRequest(context);
                }
                else if (request.Path.StartsWith(AuthGroupPrefix))
                {
                    HandleGroupRequest(context);
                }
                else if (request.Path.StartsWith(AuthAccountPrefix))
                {
                    HandleAccountRequest(context);
                } 
                
            } finally
            {
                InformationContext.ProcessAndClearCurrent();
            }
        }

        private static void ProcessDynamicRegisterRequest(HttpRequest request, HttpResponse response)
        {
            string blobPath = GetBlobPath(request);
            CloudBlob blob = StorageSupport.CurrActiveContainer.GetBlobReference(blobPath);
            response.Clear();
            try
            {
                string template = blob.DownloadText();
                string returnUrl = request.Params["ReturnUrl"];
                TBRegisterContainer registerContainer = GetRegistrationInfo(returnUrl);
                string responseContent = RenderWebSupport.RenderTemplateWithContent(template, registerContainer);
                response.ContentType = blob.Properties.ContentType;
                response.Write(responseContent);
            }
            catch (StorageClientException scEx)
            {
                response.Write(scEx.ToString());
                response.StatusCode = (int)scEx.StatusCode;
            }
            finally
            {
                response.End();
            }
        }

        private static TBRegisterContainer GetRegistrationInfo(string returnUrl)
        {
            TBRegisterContainer registerContainer = TBRegisterContainer.CreateWithLoginProviders(returnUrl, title: "Sign in", subtitle: "... or register", absoluteLoginUrl:null);
            return registerContainer;
        }

        private static string GetBlobPath(HttpRequest request)
        {
            string contentPath = request.Path;
            if (contentPath.StartsWith(AboutPrefix) == false)
                throw new NotSupportedException("Content path for other than about/ is not supported");
            return contentPath.Substring(1);
        }

        private void HandleAccountRequest(HttpContext context)
        {
            //TBRLoginRoot loginRoot = GetOrCreateLoginRoot(context);
        }

        private void HandleGroupRequest(HttpContext context)
        {
            string requestPath = context.Request.Path;
            string groupID = GetGroupID(context.Request.Path);
            string loginUrl = WebSupport.GetLoginUrl(context);
            string loginRootID = TBLoginInfo.GetLoginIDFromLoginURL(loginUrl);
            string loginGroupID = TBRLoginGroupRoot.GetLoginGroupID(groupID, loginRootID);
            TBRLoginGroupRoot loginGroupRoot = TBRLoginGroupRoot.RetrieveFromDefaultLocation(loginGroupID);
            if(loginGroupRoot == null)
            {
                // TODO: Polite invitation request
                throw new SecurityException("No access to requested group: TODO - Polite landing page for the group");
                return;
            }
            InformationContext.Current.CurrentGroupRole = loginGroupRoot.Role;
            string contentPath = requestPath.Substring(AuthGroupPrefixLen + GuidIDLen + 1);
            HandleOwnerRequest(loginGroupRoot, context, contentPath, loginGroupRoot.Role);
        }

        private string GetGroupID(string path)
        {
            return path.Substring(AuthGroupPrefixLen, GuidIDLen);
        }

        private void HandlePersonalRequest(HttpContext context)
        {
            string loginUrl = WebSupport.GetLoginUrl(context);
            TBRLoginRoot loginRoot = TBRLoginRoot.GetOrCreateLoginRootWithAccount(loginUrl, true);
            bool doDelete = false;
            if(doDelete)
            {
                loginRoot.DeleteInformationObject();
                return;
            }
            TBAccount account = loginRoot.Account;
            string requestPath = context.Request.Path;
            string contentPath = requestPath.Substring(AuthPersonalPrefixLen);
            HandleOwnerRequest(account, context, contentPath, TBCollaboratorRole.CollaboratorRoleValue);
        }

        private void HandleOwnerRequest(IContainerOwner containerOwner, HttpContext context, string contentPath, string role)
        {
            InformationContext.Current.CurrentOwner = containerOwner;
            if (context.Request.RequestType == "POST")
            {
                // Do first post, and then get to the same URL
                if (TBCollaboratorRole.HasCollaboratorRights(role) == false)
                    throw new SecurityException("Role '" + role + "' is not authorized to do changing POST requests to web interface");
                HandleOwnerPostRequest(containerOwner, context, contentPath);
                context.Response.Redirect(context.Request.Url.ToString(), true);
                return;
            }
            HandleOwnerGetRequest(containerOwner, context, contentPath);
        }

        private void HandleOwnerPostRequest(IContainerOwner containerOwner, HttpContext context, string contentPath)
        {
            HttpRequest request = context.Request;
            var form = request.Unvalidated().Form;

            bool isCancelButton = form["btnCancel"] != null;
            if (isCancelButton)
                return;
            string actionName = form["RootSourceAction"];
            if(actionName != "Save")
            {
                var result = PerformWebAction.Execute(new PerformWebActionParameters
                                                          {
                                                              CommandName = actionName,
                                                              FormSubmitContent = form,
                                                              Owner = containerOwner,
                                                          });
                return;
            }

            string submittedObjectList = form["BoundObject"];
            string[] submittedObjects = submittedObjectList.Split(',');
            foreach (string submittedObject in submittedObjects)
            {
                string[] objectWithETag = submittedObject.Split(':');
                string relativeLocation = objectWithETag[0];
                string eTag = null;
                if (objectWithETag.Length == 2)
                    eTag = objectWithETag[1];
                try
                {
                    if (containerOwner.IsMyEditableContent(relativeLocation) == false)
                        throw new SecurityException("Content not allowed to edit in this security context: " +
                                                    relativeLocation);
                    CloudBlob blob = StorageSupport.CurrActiveContainer.GetBlob(relativeLocation);
                    string objectType = blob.GetBlobInformationObjectType();
                    IInformationObject rootObject = StorageSupport.RetrieveInformation(relativeLocation, objectType,
                                                                                       eTag: eTag);
                    /* Temporarily removed all the version checks - last save wins! 
                    if (oldETag != rootObject.ETag)
                    {
                        RenderWebSupport.RefreshContent(webPageBlob);
                        throw new InvalidDataException("Information under editing was modified during display and save");
                    }
                     * */
                    // TODO: Proprely validate against only the object under the editing was changed (or its tree below)
                    rootObject.SetValuesToObjects(form);

                    // TODO: Media support through operation
                    /*
                        // If not add operation, set media content to stored object
                        foreach (string contentID in request.Files.AllKeys)
                        {
                            HttpPostedFile postedFile = request.Files[contentID];
                            if (String.IsNullOrWhiteSpace(postedFile.FileName))
                                continue;
                            rootObject.SetMediaContent(containerOwner, contentID, postedFile);
                        }
                     * */
                    rootObject.StoreInformationMasterFirst(containerOwner, false);
                }
                catch (StorageException stEx)
                {
                    if (stEx.StatusCode == HttpStatusCode.PreconditionFailed)
                    {
                        throw new DBConcurrencyException(
                            string.Format("Optimistic Concurrency Failed (Object: {0} ETag: {1})", relativeLocation,
                                          eTag));
                    }
                    throw;
                }
            }

        }

        private void HandleAboutGetRequest(HttpContext context, string contentPath)
        {
            var response = context.Response;
            var request = context.Request;
            string blobPath = GetBlobPath(request);
            CloudBlob blob = StorageSupport.CurrActiveContainer.GetBlobReference(blobPath);  //publicClient.GetBlobReference(blobPath);
            response.Clear();
            try
            {
                blob.FetchAttributes();
                response.ContentType = blob.Properties.ContentType;
                blob.DownloadToStream(response.OutputStream);
            }
            catch (StorageClientException scEx)
            {
                if (scEx.ErrorCode == StorageErrorCode.BlobNotFound || scEx.ErrorCode == StorageErrorCode.ResourceNotFound || scEx.ErrorCode == StorageErrorCode.BadRequest)
                {
                    response.Write("Blob not found or bad request: " + blob.Name + " (original path: " + request.Path + ")");
                    response.StatusCode = (int)scEx.StatusCode;
                }
                else
                {
                    response.Write("Errorcode: " + scEx.ErrorCode.ToString() + Environment.NewLine);
                    response.Write(scEx.ToString());
                    response.StatusCode = (int)scEx.StatusCode;
                }
            }
            finally
            {
                response.End();
            }

        }

        private void HandleOwnerGetRequest(IContainerOwner containerOwner, HttpContext context, string contentPath)
        {
            CloudBlob blob = StorageSupport.GetOwnerBlobReference(containerOwner, contentPath);

            // Read blob content to response.
            context.Response.Clear();
            try
            {
                blob.FetchAttributes();
                context.Response.ContentType = blob.Properties.ContentType;
                blob.DownloadToStream(context.Response.OutputStream);
            } catch(StorageClientException scEx)
            {
                if(scEx.ErrorCode == StorageErrorCode.BlobNotFound || scEx.ErrorCode == StorageErrorCode.ResourceNotFound || scEx.ErrorCode == StorageErrorCode.BadRequest)
                {
                    context.Response.Write("Blob not found or bad request: " + blob.Name + " (original path: " + context.Request.Path + ")");
                    context.Response.StatusCode = (int)scEx.StatusCode;
                } else
                {
                    context.Response.Write("Error code: " + scEx.ErrorCode.ToString() + Environment.NewLine);
                    context.Response.Write(scEx.ToString());
                    context.Response.StatusCode = (int)scEx.StatusCode;
                }
            }
            catch (Exception ex)
            {
                context.Response.Write(ex.ToString());
            }
            context.Response.End();
        }


        #endregion
    }
}
