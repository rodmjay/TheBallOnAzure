﻿using System;
using TheBall;
using TheBall.CORE;

namespace AaltoGlobalImpact.OIP
{
    public static class DeleteInformationObjectImplementation
    {
        public static void ExecuteMethod_DeleteObjectViews(IInformationObject objectToDelete)
        {
            //DefaultViewSupport.CreateDefaultViewRelativeToRequester()
        }

        public static void ExecuteMethod_DeleteObject(IInformationObject objectToDelete)
        {
            StorageSupport.DeleteInformationObject(objectToDelete);
        }
    }
}