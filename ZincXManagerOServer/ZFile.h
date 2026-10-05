#pragma once
#include <fstream>
#include <string>
#include <map>

namespace zFile
{
    class ZFile
    {
        std::string currentFile;
        std::fstream zF;
    public:
        

        bool fileOperate(const std::string& fileName, const std::string& mode);
        bool closeFile();
        std::string readFileTxt();
        std::map<std::string, std::string> readFileMap();
        std::string readFileBin();
        std::string readDir();
        bool writeFileTxt(std::string in, bool endl);
        bool writeFileTxt(std::string in, bool endl, int line);
        bool writeFileMap(std::string key, std::string value);
        std::string mapGet(const std::string& key, const std::string& def = "");
        ~ZFile();
    };

    extern ZFile logF;   // 全局对象声明，定义在 ZFile.cpp
}