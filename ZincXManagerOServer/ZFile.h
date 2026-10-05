#pragma once
#include <fstream>
#include <string>

namespace zFile
{
    class ZFile
    {
        std::fstream zF;
    public:
        std::string currentFile;

        bool fileOperate(const std::string& fileName, const std::string& mode);
        std::string readFileTxt();
        std::string readFileBin();
        std::string readDir();
        bool writeFileTxt(std::string in, bool endl);
        bool writeFileTxt(std::string in, bool endl, int line);
        bool mapEdit(std::string key, std::string value);
    };

    extern ZFile logF;   // 全局对象声明，定义在 ZFile.cpp
}