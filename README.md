    //---------------------------------------------------------------------Rebuild API---------------------------------------------------------------------------
    //If you want to change the release version, update it before in the "manifest.json" file.
    //To update this API, you first need to rebuild the solution (you can change to the "release" build [optionnal]).
    //then, copy all the files in the bin\Debug (or bin\Release if you used the "release" build) directory to the ZipContents\bin directory.
    //Now, sign the application by opening powershell on this project (right click on "TrackingApp", then "open in terminal".
    //Execute the supplied fr_appsigntool.exe :
    // ..\fr22_appsigntool\fr_appsigntool.exe ZipContents
    //Connect to the NordicId reader (https://10.11.92.136) --> user : admin / password written at the back of the reader.
    //On the left side, "Software" --> "Applications" --> Browse the zip file you just created (same directory as the project) and click "Install".
    //Good job !!
    //-----------------------------------------------------------------------------------------------------------------------------------------------------------
