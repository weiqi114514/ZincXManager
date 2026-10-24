#pragma once
#include <fstream>
#include <string>
#include <map>
#include <filesystem>
#include <vector>

namespace zFile
{
    class ZFile
    {
        std::string currentFile;
        std::fstream zF;
    public:
        

        bool fileOperate(const std::string& fileName, const std::string& mode);
        bool closeFile();
        std::vector<std::string> readFileTxt();
        std::map<std::string, std::string> readFileMap();
        std::string readFileBin();
        std::vector<std::string> readDir(const std::string& dirPath);
        bool writeFileTxt(std::string in, bool endl);
        bool writeFileTxt(std::string in, bool endl, int line);
        bool writeFileMap(std::string key, std::string value);
        std::string mapGet(const std::string& key, const std::string& def = "");
        std::string txtGet(int line);
        ~ZFile();
    };

    extern ZFile logF;   // 全局对象声明，定义在 ZFile.cpp
}