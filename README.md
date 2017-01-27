Command Line Options

  -U, --usernames    Required. Path of the file that contains usernames

  -P, --passwords    Required. Path of the file that contains passwords

  -l, --login        Required. The name of the username field in the form

  -f, --pass         Required. The name of the password field in the form

  -w, --url          Required. The URL where the form request is to be sent

  --redirect         (Default: ) If specified downloads this page on success

  -k, --check        Required. The string to be matched in the response page

  -s, --success      (Default: False) If specified, the check string match will
                     be considered as success

  -h, --header       (Default: ) The path of the file that contains the header
                     data to be sent (Format of lines Name:Value)

  -c, --cookie       (Default: ) The path of the file that contains the cookies
                     to be sent (Format of lines Name:Value)

  -d, --formdata     (Default: ) The path of the file that contains the
                     additional form data (Format of lines Name:Value)

  -o, --output       (Default: data.txt) The path of the file where the output
                     will be stored (Format Username<TAB>Password)

  -t, --threads      Required. The maximum number of threads to be executed

  -v, --verbose      (Default: False) Enable verbose mode


------------------------------------

Username and Password files contain values line separated

Header/Cookie/Form-Data file example (Name:Value):

 Accept-Encoding:gzip,deflate
 User-Agent:Mozilla/5.0 ...

 Output in Format (Username<TAB>Password):
  someone	ihaveacoolpass__
  yomamma	rockyou

Uses CommandLineParser by gsscoder