#pragma once
#include <iostream>
#include <fstream>

namespace std
{
	class ZFile
	{
		ifstream  zFin;
	public:
        bool fileOperate(const std::string& fileName, const std::string& mode)//文件操作 如果文件存在则打开，不存在则创建并打开
        {
            if (mode == "rNfC")
            {
                // 先尝试以读写方式打开
                zFin.open(fileName, std::ios::in | std::ios::out);

                if (!zFin.is_open())
                {
                    // 失败说明文件不存在，创建它
                    zFin.clear();                       // 必须清状态位
                    zFin.open(fileName, std::ios::out); // out 会创建文件
                    zFin.close();

                    // 再以读写方式打开
                    zFin.clear();
                    zFin.open(fileName, std::ios::in | std::ios::out);
                }
            }
            return zFin.is_open();
        }
	};
}
