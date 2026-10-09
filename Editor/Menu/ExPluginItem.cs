using System;
using UnityEngine;

namespace ExOpenSource.Editor
{
    [Serializable]
    public class ExPluginItem
    {

        public string Name;


        public string Version;



        public string Author;



        public string Intro;



        [HideInInspector] public string MenuPath;


        public string[] Tags;


        public string LocalPath;


        public string GitURL_Path;





        // 下载来源三项均为必填，每个插件独立保存，不继承菜单设置。
        public string GitURL_Username;





        public string GitURL_RepoName;


        public string GitURL_Branch;


        public string IntroductionURL;


        public UPMPackage[] Dependencies;
    }

    [Serializable]
    public class UPMPackage
    {

        public string name;


        public string version;
    }
}
